namespace ResilientRateLimiting;

/// <summary>Configuration for one store connection, shared by every limiter that uses it.</summary>
public sealed class StoreHealthOptions
{
    /// <summary>Polly refuses a sampling duration below this.</summary>
    internal static readonly TimeSpan MinimumSamplingDuration = TimeSpan.FromMilliseconds(500);

    /// <summary>How long to wait for the store before abandoning the call.</summary>
    public TimeSpan StoreTimeout { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>Store calls needed within <see cref="BreakerSamplingDuration"/> before the breaker may open, and only then if the failed share reaches <see cref="FailureRatio"/>. Minimum 2.</summary>
    public int FailuresBeforeOpen { get; init; } = 5;

    /// <summary>The share of store calls within <see cref="BreakerSamplingDuration"/> that must fail before the breaker opens.</summary>
    public double FailureRatio { get; init; } = 1.0;

    /// <summary>How long the breaker stays open before it probes the store again.</summary>
    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The window over which <see cref="FailuresBeforeOpen"/> is counted.</summary>
    public TimeSpan BreakerSamplingDuration { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Above this many live partitions on this store, warm state is released early for every partition, not only the excess ones.</summary>
    public int MaxWarmPartitions { get; init; } = 10_000;

    /// <summary>Scales the local budget until some limiter on this store first reaches it. 1.0 means no effect.</summary>
    public double ColdStartFallbackFactor { get; init; } = 1.0;

    /// <summary>Replicas normally running. The local fallback budget is the shared limit divided by this.</summary>
    public int ExpectedReplicaCount { get; init; }

    /// <summary>Overrides classification. True treats the exception as a store failure.</summary>
    public Func<Exception, bool>? ShouldHandle { get; init; }

    /// <summary>The per-replica budget for a shared limit of <paramref name="sharedPermitLimit"/>.</summary>
    /// <exception cref="InvalidOperationException"><see cref="ExpectedReplicaCount"/> is not set.</exception>
    public int LocalPermitLimit(int sharedPermitLimit)
    {
        if (ExpectedReplicaCount < 1)
        {
            throw new InvalidOperationException(
                $"{nameof(ExpectedReplicaCount)} must be set before asking for a local permit limit.");
        }

        return (int)Math.Ceiling(sharedPermitLimit / (double)ExpectedReplicaCount);
    }

    /// <summary>Throws when the configuration is incomplete or contradictory.</summary>
    /// <exception cref="InvalidOperationException">The configuration cannot be used.</exception>
    public void Validate()
    {
        if (StoreTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(StoreTimeout)} must be greater than zero.");
        }

        if (FailuresBeforeOpen < 2)
        {
            throw new InvalidOperationException(
                $"{nameof(FailuresBeforeOpen)} must be at least 2. The underlying breaker cannot open " +
                "on a single failure.");
        }

        if (FailureRatio is <= 0 or > 1)
        {
            throw new InvalidOperationException(
                $"{nameof(FailureRatio)} must be greater than 0 and at most 1.");
        }

        if (BreakDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(BreakDuration)} must be greater than zero.");
        }

        if (BreakerSamplingDuration < MinimumSamplingDuration)
        {
            throw new InvalidOperationException(
                $"{nameof(BreakerSamplingDuration)} must be at least {MinimumSamplingDuration.TotalMilliseconds} ms.");
        }

        if (ColdStartFallbackFactor is <= 0 or > 1)
        {
            throw new InvalidOperationException(
                $"{nameof(ColdStartFallbackFactor)} must be greater than 0 and at most 1.");
        }

        if (MaxWarmPartitions < 1)
        {
            throw new InvalidOperationException($"{nameof(MaxWarmPartitions)} must be at least 1.");
        }

        if (ExpectedReplicaCount < 1)
        {
            throw new InvalidOperationException(
                $"{nameof(ExpectedReplicaCount)} must be set. There is no safe default: the library " +
                "cannot know how many replicas you run.");
        }
    }
}
