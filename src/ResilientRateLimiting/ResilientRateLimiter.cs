using Polly.Timeout;
using System.Threading.RateLimiting;

namespace ResilientRateLimiting;

/// <summary>Wraps a store-backed rate limiter so a slow or unreachable store degrades instead of failing the request.</summary>
/// <remarks>
/// <para>Must not wrap another <see cref="ResilientRateLimiter"/>: nesting shadows the inner lease's source metadata,
/// because the outer instance always reports its own <see cref="LeaseSource"/> instead of passing the inner one through.</para>
/// <para>Call <c>AcquireAsync</c> to use this type. <c>AttemptAcquire</c> always rejects, because no store was consulted.</para>
/// <para>When the store limiter or the fallback limiter throws <see cref="ArgumentException"/>,
/// <see cref="ObjectDisposedException"/>, or <see cref="InvalidOperationException"/>, and
/// <see cref="StoreHealthOptions.ShouldHandle"/> does not say otherwise, the exception is treated as a caller
/// mistake, not a store failure: it reaches the caller instead of triggering the fallback path.</para>
/// </remarks>
public sealed class ResilientRateLimiter : RateLimiter
{
    /// <summary>The Meter name to pass to <c>AddMeter</c> when wiring up OpenTelemetry.</summary>
    public const string MeterName = "ResilientRateLimiting";

    private readonly RateLimiter _primary;
    private readonly LocalMirror? _mirror;
    private readonly ResilientRateLimiterOptions _options;
    private readonly StoreFailureBehavior _failureBehavior;
    private readonly TimeSpan _storeTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly StoreHealth _storeHealth;
    private readonly TimeSpan _recoveryTime;
    private readonly RetryAfterCalculator _retryAfter;
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
    /// <exception cref="ArgumentNullException"><paramref name="primary"/>, <paramref name="options"/>, or <paramref name="storeHealth"/> is <see langword="null"/>; or <paramref name="fallback"/> is <see langword="null"/> while <paramref name="options"/>'s <see cref="ResilientRateLimiterOptions.FailureBehavior"/> is <see cref="StoreFailureBehavior.LocalFallback"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="options"/> is incomplete or contradictory. See <see cref="ResilientRateLimiterOptions.Validate"/> for the rules that are checked.</exception>
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
        _retryAfter = new RetryAfterCalculator(options, storeHealth.Options.BreakDuration);

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
    /// <returns>Always <see langword="null"/>.</returns>
    public override RateLimiterStatistics? GetStatistics() => null;

    /// <summary>Always rejects, carrying no source tag: no store was consulted, so no path decided. The middleware calls the async path next.</summary>
    /// <param name="permitCount">Ignored.</param>
    /// <returns>A rejected lease.</returns>
    protected override RateLimitLease AttemptAcquireCore(int permitCount) => StaticLease.Rejected;

    /// <summary>Asks the store limiter first. If it is slow, unreachable, or its breaker is open, this takes the fallback path this instance was configured with instead of letting the failure reach the caller.</summary>
    /// <param name="permitCount">How many permits this request needs.</param>
    /// <param name="cancellationToken">Cancels the wait for the store. Cancelling this token is never treated as a store failure, so the fallback path is not used; the cancellation reaches the caller instead.</param>
    /// <returns>A <see cref="ResilientRateLimitLease"/> tagged with the <see cref="LeaseSource"/> of whichever path answered.</returns>
    /// <exception cref="ArgumentException">The store limiter or the fallback limiter rejected <paramref name="permitCount"/>, and this was not classified as a store failure.</exception>
    /// <exception cref="ObjectDisposedException">The store limiter or the fallback limiter has already been disposed, and this was not classified as a store failure.</exception>
    /// <exception cref="InvalidOperationException">The store limiter or the fallback limiter threw <see cref="InvalidOperationException"/>, and this was not classified as a store failure.</exception>
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

                    var computed = _retryAfter.Compute(retryAfter, LeaseSource.Recovery);
                    var refused = new ResilientRateLimitLease(StaticLease.Rejected, LeaseSource.Recovery, computed);
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

                var distributed = new ResilientRateLimitLease(lease, LeaseSource.Distributed, _retryAfter.Compute(lease, LeaseSource.Distributed));
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

    /// <summary>Disposes the primary limiter and, if one was supplied, the fallback limiter. Disposing this wrapper is the only disposal a caller needs to do; do not dispose the primary or fallback limiter separately.</summary>
    /// <param name="disposing"><see langword="true"/> when called from <see cref="IDisposable.Dispose"/> rather than a finalizer.</param>
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

    /// <summary>Disposes the primary limiter and, if one was supplied, the fallback limiter, asynchronously.</summary>
    /// <returns>A task that completes once both are disposed.</returns>
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
                new ResilientRateLimitLease(
                    StaticLease.Rejected,
                    LeaseSource.FailClosed,
                    _retryAfter.Compute(StaticLease.Rejected, LeaseSource.FailClosed)),

            _ => BuildFallbackLease(await _mirror!.AcquireAsync(permitCount, cancellationToken).ConfigureAwait(false)),
        };

    private ResilientRateLimitLease BuildFallbackLease(RateLimitLease inner) =>
        new(inner, LeaseSource.LocalFallback, _retryAfter.Compute(inner, LeaseSource.LocalFallback));
}
