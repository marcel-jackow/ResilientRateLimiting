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
    private readonly bool _ownsStoreHealth;
    private readonly TimeSpan _retention;
    private readonly int _maxWarmPartitions;
    private long _lastLocalConsumption = long.MinValue;
    private long _lastActivity = long.MinValue;
    private int _released;
    private int _inFlight;

    /// <param name="primary">The limiter backed by the shared store.</param>
    /// <param name="fallback">In-memory window or token-bucket limiter, required for <see cref="StoreFailureBehavior.LocalFallback"/>; never a <see cref="ConcurrencyLimiter"/>, which hands its permit back when the lease is disposed and so cannot hold warm state.</param>
    /// <param name="options">Configuration, validated here so a wrong setup fails at startup.</param>
    /// <param name="timeProvider">Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="storeHealth">Shared across partitions of the same store; omitting it gives this partition its own breaker and disables the warm-partition cap.</param>
    public ResilientRateLimiter(
        RateLimiter primary,
        RateLimiter? fallback,
        ResilientRateLimiterOptions options,
        TimeProvider? timeProvider = null,
        StoreHealth? storeHealth = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (options.FailureBehavior == StoreFailureBehavior.LocalFallback)
        {
            ArgumentNullException.ThrowIfNull(fallback);
        }

        _primary = primary;
        _fallback = fallback;
        _failureBehavior = options.FailureBehavior;
        _storeTimeout = options.StoreTimeout;
        _shouldHandle = options.ShouldHandle;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ownsStoreHealth = storeHealth is null;
        _storeHealth = storeHealth ?? new StoreHealth(options, _timeProvider);
        _retention = options.FallbackRecoveryTime < options.MaxWarmRetention
            ? options.FallbackRecoveryTime
            : options.MaxWarmRetention;
        _maxWarmPartitions = options.MaxWarmPartitions;

        _storeHealth.RegisterPartition();
    }

    /// <summary>Reports no idle time while a request is in flight, or while local fallback state is still held unless the store's live partition count exceeds <see cref="ResilientRateLimiterOptions.MaxWarmPartitions"/>; otherwise reports how long ago this limiter last served a request.</summary>
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
            try
            {
                var lease = await AcquireFromStoreAsync(permitCount, cancellationToken).ConfigureAwait(false);

                RecordActivity();

                if (lease.IsAcquired)
                {
                    ConsumeLocalPermit(permitCount);
                }

                return new ResilientRateLimitLease(lease, LeaseSource.Distributed);
            }
            catch (Exception exception) when (StoreFailureClassifier.IsStoreFailure(exception, _shouldHandle))
            {
                // Anything thrown from here on has nowhere left to go, and reaches the caller.
                var served = await FallbackAsync(permitCount, cancellationToken).ConfigureAwait(false);

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

        if (_ownsStoreHealth)
        {
            _storeHealth.Dispose();
        }
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

        if (_ownsStoreHealth)
        {
            _storeHealth.Dispose();
        }
    }

    private TimeSpan? OwnIdleDuration()
    {
        var lastActivity = Interlocked.Read(ref _lastActivity);

        return lastActivity == long.MinValue
            ? _primary.IdleDuration
            : _timeProvider.GetElapsedTime(lastActivity);
    }

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

    /// <summary>Mirrors an admitted request in the local counter and discards the answer.</summary>
    private void ConsumeLocalPermit(int permitCount)
    {
        if (_fallback is null || _failureBehavior != StoreFailureBehavior.LocalFallback)
        {
            return;
        }

        try
        {
            using var warm = _fallback.AttemptAcquire(permitCount);
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

    private async ValueTask<RateLimitLease> AcquireFromFallbackAsync(int permitCount, CancellationToken cancellationToken)
    {
        var lease = await _fallback!.AcquireAsync(permitCount, cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _lastLocalConsumption, _timeProvider.GetTimestamp());
        return lease;
    }
}
