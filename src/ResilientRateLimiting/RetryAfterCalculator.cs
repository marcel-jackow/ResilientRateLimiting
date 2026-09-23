using System.Threading.RateLimiting;

namespace ResilientRateLimiting;

/// <summary>Produces a jittered <c>Retry-After</c> so blocked callers do not retry together. Never throws.</summary>
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

        if (degraded)
        {
            baseValue = SaturatingScale(baseValue, 2);
        }

        var (minJitter, maxJitter) = degraded ? (0.20, 0.40) : (0.10, 0.20);
        var jitter = minJitter + (_sampler() * (maxJitter - minJitter));
        var jittered = SaturatingScale(baseValue, 1 + jitter);

        return jittered < Floor ? Floor : jittered;
    }

    private TimeSpan Estimate() => options.FallbackRecoveryTime > TimeSpan.Zero ? options.FallbackRecoveryTime : breakDuration;

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
}
