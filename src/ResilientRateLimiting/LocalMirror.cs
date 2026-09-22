using System.Threading.RateLimiting;

namespace ResilientRateLimiting;

/// <summary>The in-memory counter behind one partition: it mirrors what this replica admitted while the store answered, and serves the request when the store cannot.</summary>
internal sealed class LocalMirror : IDisposable, IAsyncDisposable
{
    private readonly RateLimiter _fallback;
    private readonly StoreHealth _storeHealth;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _warmRetention;
    private readonly int _maxWarmPartitions;
    private readonly double _coldStartFactor;
    private long _lastConsumption = long.MinValue;

    internal LocalMirror(
        RateLimiter fallback,
        ResilientRateLimiterOptions options,
        StoreHealth storeHealth,
        TimeProvider timeProvider)
    {
        if (fallback is ConcurrencyLimiter)
        {
            throw new ArgumentException(
                "A ConcurrencyLimiter cannot serve as the local fallback: its leases return the permit on dispose, so no warm state can be held.",
                nameof(fallback));
        }

        _fallback = fallback;
        _storeHealth = storeHealth;
        _timeProvider = timeProvider;
        _warmRetention = options.FallbackRecoveryTime < options.MaxWarmRetention
            ? options.FallbackRecoveryTime
            : options.MaxWarmRetention;
        _maxWarmPartitions = storeHealth.Options.MaxWarmPartitions;
        _coldStartFactor = storeHealth.Options.ColdStartFallbackFactor;
    }

    /// <summary>Whether this partition still holds local state worth keeping from the framework's sweep. Reads timestamps and counters only, never the fallback limiter, because a throwing getter aborts the whole sweep.</summary>
    internal bool HoldsWarmState
    {
        get
        {
            var lastConsumption = Interlocked.Read(ref _lastConsumption);

            return lastConsumption != long.MinValue
                && _storeHealth.LivePartitions <= _maxWarmPartitions
                && _timeProvider.GetElapsedTime(lastConsumption) < _warmRetention;
        }
    }

    /// <summary>Takes permits and reports what the counter said. A counter that cannot answer decides nothing, so the store decides instead.</summary>
    internal LocalVerdict Charge(int permitCount, out TimeSpan? retryAfter)
    {
        retryAfter = null;

        try
        {
            using var local = _fallback.AttemptAcquire(permitCount);
            Touch();

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
            return LocalVerdict.NotConsulted;
        }
    }

    /// <summary>Serves the request from the counter alone. Until some limiter on this store connection has been answered by the store, the charge is scaled up: an empty counter in a process that has never been answered is not evidence of an empty share.</summary>
    internal async ValueTask<RateLimitLease> AcquireAsync(int permitCount, CancellationToken cancellationToken)
    {
        var permits = ColdStartPermits(permitCount);
        var lease = await TryAcquireAsync(permits, cancellationToken).ConfigureAwait(false);

        if (lease is null && permits != permitCount)
        {
            lease = await TryAcquireAsync(permitCount, cancellationToken).ConfigureAwait(false);
        }

        if (lease is null)
        {
            // The limiter was never entered, so this partition holds no warm state to protect.
            return StaticLease.Rejected;
        }

        Touch();

        return lease;
    }

    /// <inheritdoc />
    public void Dispose() => _fallback.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _fallback.DisposeAsync();

    /// <summary>Marks warm state as held, and only ever where the counter actually answered.</summary>
    private void Touch() => Interlocked.Exchange(ref _lastConsumption, _timeProvider.GetTimestamp());

    /// <summary>Returns <see langword="null"/> when the counter cannot grant this many permits at all, which is a rejection rather than a failure of the request.</summary>
    private async ValueTask<RateLimitLease?> TryAcquireAsync(int permits, CancellationToken cancellationToken)
    {
        try
        {
            return await _fallback.AcquireAsync(permits, cancellationToken).ConfigureAwait(false);
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
}

internal enum LocalVerdict
{
    NotConsulted,
    Granted,
    Refused,
}
