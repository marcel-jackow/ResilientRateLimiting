namespace ResilientRateLimiting;

/// <summary>Configuration for <see cref="ResilientRateLimiter"/>.</summary>
public sealed class ResilientRateLimiterOptions
{
    /// <summary>Polly refuses a sampling duration below this.</summary>
    internal static readonly TimeSpan MinimumSamplingDuration = TimeSpan.FromMilliseconds(500);

    /// <summary>How long to wait for the store before abandoning the call.</summary>
    public TimeSpan StoreTimeout { get; set; } = TimeSpan.FromMilliseconds(20);

    /// <summary>Store calls needed within <see cref="BreakerSamplingDuration"/> before the breaker may open, and only then if the failed share reaches <see cref="FailureRatio"/>. Minimum 2.</summary>
    public int FailuresBeforeOpen { get; set; } = 5;

    /// <summary>The share of store calls within <see cref="BreakerSamplingDuration"/> that must fail before the breaker opens.</summary>
    public double FailureRatio { get; set; } = 1.0;

    /// <summary>How long the breaker stays open before it probes the store again.</summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The window over which <see cref="FailuresBeforeOpen"/> is counted.</summary>
    public TimeSpan BreakerSamplingDuration { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>What to do when the store cannot answer.</summary>
    public StoreFailureBehavior FailureBehavior { get; set; } = StoreFailureBehavior.LocalFallback;

    /// <summary>Replicas normally running. The local fallback budget is the shared limit divided by this.</summary>
    public int ExpectedReplicaCount { get; set; }

    /// <summary>How long the fallback limiter takes to refill completely. Required; a limiter cannot be asked.</summary>
    public TimeSpan FallbackRecoveryTime { get; set; }

    /// <summary>Upper bound on how long warm fallback state is held for an idle partition.</summary>
    public TimeSpan MaxWarmRetention { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Above this many live partitions on the shared <see cref="StoreHealth"/>, warm state is released early for every partition, not only the excess ones.</summary>
    public int MaxWarmPartitions { get; set; } = 10_000;

    /// <summary>Scales the local budget until this process first reaches the store. 1.0 means no effect.</summary>
    public double ColdStartFallbackFactor { get; set; } = 1.0;

    /// <summary>Whether to tell the caller, in a response header, that limiting is degraded.</summary>
    public bool EmitDegradedHeader { get; set; }

    /// <summary>Tag metrics with the partition key. One time series per key — unsafe for IP addresses.</summary>
    public bool TagMetricsByPartitionKey { get; set; }

    /// <summary>Low-cardinality metric tag identifying this policy.</summary>
    public string PolicyName { get; set; } = "default";

    /// <summary>Overrides classification. True treats the exception as a store failure.</summary>
    public Func<Exception, bool>? ShouldHandle { get; set; }

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

        if (MaxWarmRetention <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(MaxWarmRetention)} must be greater than zero.");
        }

        if (FailureBehavior != StoreFailureBehavior.LocalFallback)
        {
            return;
        }

        if (ExpectedReplicaCount < 1)
        {
            throw new InvalidOperationException(
                $"{nameof(ExpectedReplicaCount)} must be set when {nameof(FailureBehavior)} is " +
                $"{nameof(StoreFailureBehavior.LocalFallback)}. There is no safe default: the library " +
                "cannot know how many replicas you run.");
        }

        if (FallbackRecoveryTime <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{nameof(FallbackRecoveryTime)} must be set when {nameof(FailureBehavior)} is " +
                $"{nameof(StoreFailureBehavior.LocalFallback)}. A RateLimiter cannot be asked how long " +
                "it takes to refill.");
        }
    }
}
