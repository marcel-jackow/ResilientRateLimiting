using Polly.Timeout;
using System.Threading.RateLimiting;

namespace ResilientRateLimiting;

/// <summary>Wraps a store-backed rate limiter so a slow or unreachable store degrades instead of failing the request.</summary>
/// <remarks>Must not wrap another <see cref="ResilientRateLimiter"/>: nesting shadows the inner lease's source metadata.</remarks>
public sealed class ResilientRateLimiter : RateLimiter
{
    private readonly RateLimiter _primary;
    private readonly RateLimiter? _fallback;
    private readonly StoreFailureBehavior _failureBehavior;
    private readonly TimeSpan _storeTimeout;
    private readonly Func<Exception, bool>? _shouldHandle;
    private readonly TimeProvider _timeProvider;
    private readonly StoreHealth _storeHealth;
    private readonly TimeSpan _retention;
    private readonly TimeSpan _recoveryTime;
    private readonly int _maxWarmPartitions;
    private readonly bool _warmFallback;
    private readonly double _coldStartFactor;
    private long _lastLocalConsumption = long.MinValue;
    private long _recoveryUntil = long.MinValue;
    private long _lastFallbackAt = long.MinValue;
    private long _lastActivity;
    private int _released;
    private int _inFlight;

    /// <param name="primary">The limiter backed by the shared store.</param>
    /// <param name="fallback">In-memory window or token-bucket limiter, required for <see cref="StoreFailureBehavior.LocalFallback"/>; never a <see cref="ConcurrencyLimiter"/>, which hands its permit back when the lease is disposed and so cannot hold warm state.</param>
    /// <param name="options">Configuration, validated here so a wrong setup fails at startup.</param>
    /// <param name="storeHealth">One per store connection, shared by every limiter using that store; each limiter must be disposed, because the shared live-partition count only falls on disposal.</param>
    /// <param name="timeProvider">Defaults to <see cref="TimeProvider.System"/>.</param>
    public ResilientRateLimiter(
        RateLimiter primary,
        RateLimiter? fallback,
        ResilientRateLimiterOptions options,
        StoreHealth storeHealth,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(storeHealth);
        options.Validate();

        if (options.FailureBehavior == StoreFailureBehavior.LocalFallback)
        {
            ArgumentNullException.ThrowIfNull(fallback);
        }

        if (fallback is ConcurrencyLimiter)
        {
            throw new ArgumentException(
                "A ConcurrencyLimiter cannot serve as the local fallback: its leases return the permit on dispose, so no warm state can be held.",
                nameof(fallback));
        }

        _primary = primary;
        _fallback = fallback;
        _failureBehavior = options.FailureBehavior;
        _storeTimeout = options.StoreTimeout;
        _shouldHandle = options.ShouldHandle;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastActivity = _timeProvider.GetTimestamp();
        _storeHealth = storeHealth;
        _retention = options.FallbackRecoveryTime < options.MaxWarmRetention
            ? options.FallbackRecoveryTime
            : options.MaxWarmRetention;
        _recoveryTime = options.FallbackRecoveryTime;
        _maxWarmPartitions = options.MaxWarmPartitions;
        _warmFallback = fallback is not null && options.FailureBehavior == StoreFailureBehavior.LocalFallback;
        _coldStartFactor = options.ColdStartFallbackFactor;

        _storeHealth.RegisterPartition();
    }

    /// <summary>Reports no idle time while a request is in flight, or while local fallback state is still held unless the store's live partition count exceeds <see cref="ResilientRateLimiterOptions.MaxWarmPartitions"/>; otherwise reports how long ago this limiter last served a request, or was created if it has served none.</summary>
    public override TimeSpan? IdleDuration
    {
        get
        {
            if (Volatile.Read(ref _inFlight) > 0)
            {
                return null;
            }

            if (_storeHealth.LivePartitions > _maxWarmPartitions)
            {
                return OwnIdleDuration();
            }

            var lastConsumption = Interlocked.Read(ref _lastLocalConsumption);

            if (lastConsumption == long.MinValue)
            {
                return OwnIdleDuration();
            }

            return _timeProvider.GetElapsedTime(lastConsumption) >= _retention
                ? OwnIdleDuration()
                : null;
        }
    }

    /// <summary>Always <see langword="null"/>: the decorator keeps no counters of its own, and the store limiter answers for the shared state.</summary>
    public override RateLimiterStatistics? GetStatistics() => null;

    /// <summary>Always rejects, carrying no source tag: no store was consulted, so no path decided. The middleware calls the async path next.</summary>
    protected override RateLimitLease AttemptAcquireCore(int permitCount) => StaticLease.Rejected;

    /// <inheritdoc />
    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _inFlight);

        try
        {
            var verdict = LocalVerdict.NotConsulted;
            TimeSpan? retryAfter = null;

            if (IsInRecovery())
            {
                verdict = ConsultLocalCounter(permitCount, out retryAfter);
            }

            if (verdict == LocalVerdict.Refused)
            {
                RecordActivity();

                return new ResilientRateLimitLease(StaticLease.Rejected, LeaseSource.LocalFallback, retryAfter);
            }

            try
            {
                var lease = await AcquireFromStoreAsync(permitCount, cancellationToken).ConfigureAwait(false);

                _storeHealth.MarkReached();

                RecordActivity();

                if (lease.IsAcquired && verdict != LocalVerdict.Granted)
                {
                    ConsumeLocalPermit(permitCount);
                }

                ArmRecovery();

                return new ResilientRateLimitLease(lease, LeaseSource.Distributed);
            }
            catch (Exception exception) when (StoreFailureClassifier.IsStoreFailure(exception, _shouldHandle))
            {
                Interlocked.Exchange(ref _lastFallbackAt, _timeProvider.GetTimestamp());

                // Anything thrown from here on has nowhere left to go, and reaches the caller.
                var served = verdict == LocalVerdict.Granted
                    ? new ResilientRateLimitLease(StaticLease.Acquired, LeaseSource.LocalFallback)
                    : await FallbackAsync(permitCount, cancellationToken).ConfigureAwait(false);

                RecordActivity();

                return served;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        ReleasePartition();

        _primary.Dispose();
        _fallback?.Dispose();
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        ReleasePartition();

        await _primary.DisposeAsync().ConfigureAwait(false);

        if (_fallback is not null)
        {
            await _fallback.DisposeAsync().ConfigureAwait(false);
        }
    }

    private TimeSpan OwnIdleDuration() => _timeProvider.GetElapsedTime(Interlocked.Read(ref _lastActivity));

    private void RecordActivity() => Interlocked.Exchange(ref _lastActivity, _timeProvider.GetTimestamp());

    private void ReleasePartition()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _storeHealth.ReleasePartition();
        }
    }

    private async Task<RateLimitLease> AcquireFromStoreAsync(int permitCount, CancellationToken cancellationToken) =>
        await _storeHealth.Pipeline
            .ExecuteAsync(
                async token => await RaceAgainstCutoffAsync(permitCount, token).ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<RateLimitLease> RaceAgainstCutoffAsync(int permitCount, CancellationToken token)
    {
        var storeToken = CancellationTokenSource.CreateLinkedTokenSource(token);
        var abandoned = false;

        try
        {
            var storeCall = _primary.AcquireAsync(permitCount, storeToken.Token).AsTask();
            var expired = Task.Delay(_storeTimeout, _timeProvider, storeToken.Token);

            if (await Task.WhenAny(storeCall, expired).ConfigureAwait(false) == storeCall)
            {
                await storeToken.CancelAsync().ConfigureAwait(false);
                return await storeCall.ConfigureAwait(false);
            }

            // From here the source belongs to the continuation, which outlives this method.
            abandoned = true;

            try
            {
                await storeToken.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                Abandon(storeCall, storeToken);
            }

            token.ThrowIfCancellationRequested();

            throw new TimeoutRejectedException($"The store did not answer within {_storeTimeout}.");
        }
        finally
        {
            if (!abandoned)
            {
                storeToken.Dispose();
            }
        }
    }

    private bool IsInRecovery()
    {
        var until = Interlocked.Read(ref _recoveryUntil);

        return until != long.MinValue && _timeProvider.GetTimestamp() < until;
    }

    /// <summary>Opens recovery mode when a call succeeds after a fallback within the last recovery time. The claim is consumed atomically, so one outage arms one window.</summary>
    private void ArmRecovery()
    {
        if (!_warmFallback)
        {
            return;
        }

        var fellBackAt = Interlocked.Exchange(ref _lastFallbackAt, long.MinValue);

        if (fellBackAt == long.MinValue || _timeProvider.GetElapsedTime(fellBackAt) >= _recoveryTime)
        {
            return;
        }

        Interlocked.Exchange(ref _recoveryUntil, RecoveryDeadline());
    }

    private long RecoveryDeadline()
    {
        var now = _timeProvider.GetTimestamp();
        var ticks = _recoveryTime.TotalSeconds * _timeProvider.TimestampFrequency;

        return ticks >= long.MaxValue - now ? long.MaxValue : now + (long)ticks;
    }

    /// <summary>While recovering, the local counter answers first: it already carries what this replica spent while the store was blind, so honouring its refusal suppresses the overshoot without writing anything back. A grant here is the request's only local charge.</summary>
    private LocalVerdict ConsultLocalCounter(int permitCount, out TimeSpan? retryAfter)
    {
        retryAfter = null;

        try
        {
            using var local = _fallback!.AttemptAcquire(permitCount);
            Interlocked.Exchange(ref _lastLocalConsumption, _timeProvider.GetTimestamp());

            if (local.IsAcquired)
            {
                return LocalVerdict.Granted;
            }

            if (local.TryGetMetadata(MetadataName.RetryAfter, out var value))
            {
                retryAfter = value;
            }

            return LocalVerdict.Refused;
        }
        catch
        {
            // A local counter that cannot answer must not reject the request: the store decides.
            return LocalVerdict.NotConsulted;
        }
    }

    /// <summary>Mirrors an admitted request in the local counter and discards the answer.</summary>
    private void ConsumeLocalPermit(int permitCount)
    {
        if (!_warmFallback)
        {
            return;
        }

        try
        {
            using var warm = _fallback!.AttemptAcquire(permitCount);
            Interlocked.Exchange(ref _lastLocalConsumption, _timeProvider.GetTimestamp());
        }
        catch
        {
            // Deliberately empty: the store already charged the caller, so a mirror failure must not surface.
        }
    }

    private static void Abandon(Task<RateLimitLease> storeCall, CancellationTokenSource storeToken) =>
        _ = storeCall.ContinueWith(
            static (completed, state) =>
            {
                try
                {
                    _ = completed.Exception;

                    if (completed.Status == TaskStatus.RanToCompletion)
                    {
                        completed.Result.Dispose();
                    }
                }
                catch
                {
                    // The caller was served from the fallback; a failure releasing an abandoned
                    // lease has nowhere useful to go.
                }
                finally
                {
                    ((CancellationTokenSource)state!).Dispose();
                }
            },
            storeToken,
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

    private async ValueTask<RateLimitLease> FallbackAsync(int permitCount, CancellationToken cancellationToken) =>
        _failureBehavior switch
        {
            StoreFailureBehavior.FailOpen =>
                new ResilientRateLimitLease(StaticLease.Acquired, LeaseSource.FailOpen),

            StoreFailureBehavior.FailClosed =>
                new ResilientRateLimitLease(StaticLease.Rejected, LeaseSource.FailClosed),

            _ => new ResilientRateLimitLease(
                await AcquireFromFallbackAsync(permitCount, cancellationToken).ConfigureAwait(false),
                LeaseSource.LocalFallback),
        };

    /// <summary>Charges the local counter more than the caller asked for until some limiter on this store connection has been answered by the store: an empty counter in a process that has never been answered is not evidence of an empty share.</summary>
    private async ValueTask<RateLimitLease> AcquireFromFallbackAsync(int permitCount, CancellationToken cancellationToken)
    {
        var permits = ColdStartPermits(permitCount);
        var lease = await TryAcquireLocallyAsync(permits, cancellationToken).ConfigureAwait(false);

        if (lease is null && permits != permitCount)
        {
            lease = await TryAcquireLocallyAsync(permitCount, cancellationToken).ConfigureAwait(false);
        }

        Interlocked.Exchange(ref _lastLocalConsumption, _timeProvider.GetTimestamp());

        return lease ?? StaticLease.Rejected;
    }

    /// <summary>Returns <see langword="null"/> when the local limiter cannot grant this many permits at all, which is a rejection rather than a failure of the request.</summary>
    private async ValueTask<RateLimitLease?> TryAcquireLocallyAsync(int permits, CancellationToken cancellationToken)
    {
        try
        {
            return await _fallback!.AcquireAsync(permits, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Every limiter throws above its own permit limit, and the per-replica budget is
            // smaller than the shared one by construction, so this input is ordinary.
            return null;
        }
    }

    private int ColdStartPermits(int permitCount)
    {
        if (_storeHealth.HasBeenReached)
        {
            return permitCount;
        }

        var scaled = Math.Ceiling(permitCount / _coldStartFactor);

        return scaled >= int.MaxValue ? int.MaxValue : (int)scaled;
    }

    private enum LocalVerdict
    {
        NotConsulted,
        Granted,
        Refused,
    }
}
