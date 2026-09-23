namespace ResilientRateLimiting;

/// <summary>Configuration for one store connection, shared by every limiter that uses it.</summary>
public sealed record StoreHealthOptions
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

    /// <summary>Overrides classification. True treats the exception as a store failure.</summary>
    public Func<Exception, bool>? ShouldHandle { get; init; }

    /// <summary>Raised once per distinct exception type per outage when a store call fails on any limiter sharing this connection.</summary>
    public Action<Exception>? OnStoreFailure { get; init; }

    /// <summary>Throws when the configuration is incomplete or contradictory. Reports every violated rule, not just the first.</summary>
    /// <exception cref="InvalidOperationException">The configuration cannot be used. The message lists every violated rule, one per line.</exception>
    public void Validate()
    {
        List<string>? errors = null;

        if (StoreTimeout <= TimeSpan.Zero)
        {
            (errors ??= []).Add($"{nameof(StoreTimeout)} must be greater than zero.");
        }

        if (FailuresBeforeOpen < 2)
        {
            (errors ??= []).Add(
                $"{nameof(FailuresBeforeOpen)} must be at least 2. The underlying breaker cannot open " +
                "on a single failure.");
        }

        if (FailureRatio is <= 0 or > 1)
        {
            (errors ??= []).Add($"{nameof(FailureRatio)} must be greater than 0 and at most 1.");
        }

        if (BreakDuration <= TimeSpan.Zero)
        {
            (errors ??= []).Add($"{nameof(BreakDuration)} must be greater than zero.");
        }

        if (BreakerSamplingDuration < MinimumSamplingDuration)
        {
            (errors ??= []).Add(
                $"{nameof(BreakerSamplingDuration)} must be at least {MinimumSamplingDuration.TotalMilliseconds} ms.");
        }

        if (ColdStartFallbackFactor is <= 0 or > 1)
        {
            (errors ??= []).Add($"{nameof(ColdStartFallbackFactor)} must be greater than 0 and at most 1.");
        }

        if (MaxWarmPartitions < 1)
        {
            (errors ??= []).Add($"{nameof(MaxWarmPartitions)} must be at least 1.");
        }

        if (errors is not null)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }
    }
}
