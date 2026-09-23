using Polly.Timeout;
using System.Threading.RateLimiting;

namespace ResilientRateLimiting;

/// <summary>Wraps a store-backed rate limiter so a slow or unreachable store degrades instead of failing the request.</summary>
/// <remarks>Must not wrap another <see cref="ResilientRateLimiter"/>: nesting shadows the inner lease's source metadata.</remarks>
public sealed class ResilientRateLimiter : RateLimiter
{
    private readonly RateLimiter _primary;
    private readonly LocalMirror? _mirror;
    private readonly ResilientRateLimiterOptions _options;
    private readonly StoreFailureBehavior _failureBehavior;
    private readonly TimeSpan _storeTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly StoreHealth _storeHealth;
    private readonly TimeSpan _recoveryTime;
    private long _recoveryArmedAt = long.MinValue;
    private long _lastFallbackAt = long.MinValue;
    private long _lastActivity;
    private int _released;
    private int _inFlight;

    /// <param name="primary">The limiter backed by the shared store.</param>
    /// <param name="fallback">In-memory window or token-bucket limiter, required for <see cref="StoreFailureBehavior.LocalFallback"/>; never a <see cref="ConcurrencyLimiter"/>, which hands its permit back when the lease is disposed and so cannot hold warm state.</param>
    /// <param name="options">Configuration for this limiter, validated here so a wrong setup fails at startup. Everything shared by the store connection comes from <paramref name="storeHealth"/>.</param>
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

        _primary = primary;
        _options = options;
        _failureBehavior = options.FailureBehavior;
        _storeTimeout = storeHealth.Options.StoreTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastActivity = _timeProvider.GetTimestamp();
        _storeHealth = storeHealth;
        _recoveryTime = options.FallbackRecoveryTime;

        // Built whenever a fallback was supplied, not only when it is consulted, so that a
        // fail-open limiter still disposes the limiter it was handed.
        _mirror = fallback is null ? null : new LocalMirror(fallback, options, storeHealth, _timeProvider);

        _storeHealth.RegisterPartition();
    }

    /// <summary>Reports no idle time while a request is in flight, or while local fallback state is still held unless the store's live partition count exceeds <see cref="StoreHealthOptions.MaxWarmPartitions"/>; otherwise reports how long ago this limiter last served a request, or was created if it has served none.</summary>
    public override TimeSpan? IdleDuration
    {
        get
        {
            if (Volatile.Read(ref _inFlight) > 0)
            {
                return null;
            }

            return _mirror?.HoldsWarmState == true ? null : OwnIdleDuration();
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

            if (IsInRecovery() && _mirror is { } gate)
            {
                verdict = gate.Charge(permitCount, out var retryAfter);

                if (verdict == LocalVerdict.Refused)
                {
                    RecordActivity();

                    var refused = new ResilientRateLimitLease(StaticLease.Rejected, LeaseSource.Recovery, retryAfter);
                    ResilientRateLimiterMetrics.Shared.RecordLease(_options, refused.Source, refused.IsAcquired);
                    return refused;
                }
            }

            try
            {
                var lease = await _storeHealth.Pipeline
                    .ExecuteAsync(
                        static (state, token) => state.Limiter.RaceAgainstCutoffAsync(state.PermitCount, token),
                        (Limiter: this, PermitCount: permitCount),
                        cancellationToken)
                    .ConfigureAwait(false);

                _storeHealth.MarkReached();

                RecordActivity();

                if (lease.IsAcquired && verdict != LocalVerdict.Granted)
                {
                    ConsumeLocalPermit(permitCount);
                }

                ArmRecovery();

                var distributed = new ResilientRateLimitLease(lease, LeaseSource.Distributed);
                ResilientRateLimiterMetrics.Shared.RecordLease(_options, distributed.Source, distributed.IsAcquired);
                return distributed;
            }
            catch (Exception exception) when (_storeHealth.IsStoreFailure(exception))
            {
                Interlocked.Exchange(ref _lastFallbackAt, _timeProvider.GetTimestamp());

                ResilientRateLimiterMetrics.Shared.RecordStoreFailure(_options, exception);
                _storeHealth.ReportFirstOccurrence(exception);

                // Anything thrown from here on has nowhere left to go, and reaches the caller.
                var served = verdict == LocalVerdict.Granted
                    ? new ResilientRateLimitLease(StaticLease.Acquired, LeaseSource.LocalFallback)
                    : await FallbackAsync(permitCount, cancellationToken).ConfigureAwait(false);

                RecordActivity();

                ResilientRateLimiterMetrics.Shared.RecordLease(_options, served.Source, served.IsAcquired);

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
        _mirror?.Dispose();
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        ReleasePartition();

        await _primary.DisposeAsync().ConfigureAwait(false);

        if (_mirror is not null)
        {
            await _mirror.DisposeAsync().ConfigureAwait(false);
        }
    }

    private bool IsWarmFallback => _failureBehavior == StoreFailureBehavior.LocalFallback;

    private TimeSpan OwnIdleDuration() => _timeProvider.GetElapsedTime(Interlocked.Read(ref _lastActivity));

    private void RecordActivity() => Interlocked.Exchange(ref _lastActivity, _timeProvider.GetTimestamp());

    private void ReleasePartition()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _storeHealth.ReleasePartition();
        }
    }

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
        var armedAt = Interlocked.Read(ref _recoveryArmedAt);

        return armedAt != long.MinValue && _timeProvider.GetElapsedTime(armedAt) < _recoveryTime;
    }

    /// <summary>Opens recovery mode when a call succeeds after a fallback within the last recovery time. The claim is consumed atomically, so one outage arms one window.</summary>
    private void ArmRecovery()
    {
        if (!IsWarmFallback)
        {
            return;
        }

        var fellBackAt = Interlocked.Exchange(ref _lastFallbackAt, long.MinValue);

        if (fellBackAt == long.MinValue || _timeProvider.GetElapsedTime(fellBackAt) >= _recoveryTime)
        {
            return;
        }

        Interlocked.Exchange(ref _recoveryArmedAt, _timeProvider.GetTimestamp());
    }

    /// <summary>Mirrors an admitted request in the local counter and discards the answer.</summary>
    private void ConsumeLocalPermit(int permitCount)
    {
        if (IsWarmFallback && _mirror is { } mirror)
        {
            _ = mirror.Charge(permitCount, out _);
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

    private async ValueTask<ResilientRateLimitLease> FallbackAsync(int permitCount, CancellationToken cancellationToken) =>
        _failureBehavior switch
        {
            StoreFailureBehavior.FailOpen =>
                new ResilientRateLimitLease(StaticLease.Acquired, LeaseSource.FailOpen),

            StoreFailureBehavior.FailClosed =>
                new ResilientRateLimitLease(StaticLease.Rejected, LeaseSource.FailClosed),

            _ => new ResilientRateLimitLease(
                await _mirror!.AcquireAsync(permitCount, cancellationToken).ConfigureAwait(false),
                LeaseSource.LocalFallback),
        };
}
