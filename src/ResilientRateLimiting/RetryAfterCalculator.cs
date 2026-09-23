using System.Threading.RateLimiting;

namespace ResilientRateLimiting;

/// <summary>Produces a <c>Retry-After</c> so blocked callers do not retry together, adding up to <see cref="ResilientRateLimiterOptions.MaxAddedRetryDelay"/>. Never throws.</summary>
internal sealed class RetryAfterCalculator(ResilientRateLimiterOptions options, TimeSpan breakDuration, Func<double>? sampler = null)
{
    private static readonly TimeSpan Floor = TimeSpan.FromSeconds(1);

    private readonly Func<double> _sampler = sampler ?? Random.Shared.NextDouble;

    /// <summary>For a path that hands over an actual lease to read metadata from.</summary>
    public TimeSpan? Compute(RateLimitLease innerLease, LeaseSource source) =>
        innerLease.IsAcquired ? null : Compute(ReadRetryAfter(innerLease), source);

    /// <summary>For a path whose inner value was already extracted elsewhere (the recovery gate disposes its lease before this is called).</summary>
    public TimeSpan? Compute(TimeSpan? innerRetryAfter, LeaseSource source)
    {
        var degraded = source != LeaseSource.Distributed;
        var baseValue = innerRetryAfter ?? Estimate();
        var cap = options.MaxAddedRetryDelay;

        var result = cap > TimeSpan.Zero ? AddDelay(baseValue, degraded, cap) : baseValue;

        return result < Floor ? Floor : result;
    }

    private TimeSpan Estimate() => options.FallbackRecoveryTime > TimeSpan.Zero ? options.FallbackRecoveryTime : breakDuration;

    /// <summary>
    /// Adds a random spread and, while degraded, a longer wait — both drawn from a band computed on the
    /// pre-doubling base value, and both capped so the total never exceeds <paramref name="cap"/>. The
    /// spread gets the room in the cap first; the extra degraded wait takes what is left over.
    /// </summary>
    private TimeSpan AddDelay(TimeSpan baseValue, bool degraded, TimeSpan cap)
    {
        var (minPercent, minFloor, maxPercent, maxFloor) = degraded
            ? (0.20, TimeSpan.FromSeconds(2), 0.40, TimeSpan.FromSeconds(6))
            : (0.10, TimeSpan.FromSeconds(1), 0.20, TimeSpan.FromSeconds(3));

        var lo = Max(SaturatingScale(baseValue, minPercent), minFloor);
        var hi = Max(SaturatingScale(baseValue, maxPercent), maxFloor);

        var hiPrime = Min(hi, cap);
        var loPrime = Min(lo, Half(hiPrime));

        var jitter = loPrime + SaturatingScale(hiPrime - loPrime, _sampler());
        var doubling = degraded ? Min(baseValue, cap - hiPrime) : TimeSpan.Zero;

        return SaturatingAdd(SaturatingAdd(baseValue, doubling), jitter);
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;

    private static TimeSpan Half(TimeSpan value) => TimeSpan.FromTicks(value.Ticks / 2);

    /// <summary>Reading metadata is caller-supplied code (a custom inner limiter); a throwing read counts as no value.</summary>
    private static TimeSpan? ReadRetryAfter(RateLimitLease innerLease)
    {
        try
        {
            return innerLease.TryGetMetadata(MetadataName.RetryAfter.Name, out var metadata) && metadata is TimeSpan value
                ? value
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Multiplies without throwing <see cref="OverflowException"/> when <paramref name="value"/> is close to <see cref="TimeSpan.MaxValue"/>.</summary>
    private static TimeSpan SaturatingScale(TimeSpan value, double factor)
    {
        var ticks = value.Ticks * factor;
        return ticks >= TimeSpan.MaxValue.Ticks ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>Adds without throwing <see cref="OverflowException"/> when the sum is close to <see cref="TimeSpan.MaxValue"/>.</summary>
    private static TimeSpan SaturatingAdd(TimeSpan left, TimeSpan right)
    {
        var ticks = (double)left.Ticks + right.Ticks;
        return ticks >= TimeSpan.MaxValue.Ticks ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)ticks);
    }
}
