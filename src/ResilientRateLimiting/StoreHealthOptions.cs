namespace ResilientRateLimiting;

/// <summary>Configuration for one store connection, shared by every limiter that uses it.</summary>
public sealed record StoreHealthOptions
{
    /// <summary>Polly refuses a sampling duration below this.</summary>
    internal static readonly TimeSpan MinimumSamplingDuration = TimeSpan.FromMilliseconds(500);

    /// <summary>How long to wait for the store before abandoning the call.</summary>
    /// <remarks>Default: 20 milliseconds. Must be greater than zero.</remarks>
    public TimeSpan StoreTimeout { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>Store calls needed within <see cref="BreakerSamplingDuration"/> before the breaker may open, and only then if the failed share reaches <see cref="FailureRatio"/>. Minimum 2.</summary>
    /// <remarks>Default: 5.</remarks>
    public int FailuresBeforeOpen { get; init; } = 5;

    /// <summary>The share of store calls within <see cref="BreakerSamplingDuration"/> that must fail before the breaker opens. Default 0.5: the breaker opens once at least half of the calls failed.</summary>
    /// <remarks>Must be greater than zero and at most one (a share of one means every call must fail).</remarks>
    public double FailureRatio { get; init; } = 0.5;

    /// <summary>How long the breaker stays open before it probes the store again.</summary>
    /// <remarks>Default: 5 seconds. Must be greater than zero.</remarks>
    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The window over which <see cref="FailuresBeforeOpen"/> is counted.</summary>
    /// <remarks>Default: 10 seconds. Must be at least 500 milliseconds; the underlying breaker library refuses a shorter window.</remarks>
    public TimeSpan BreakerSamplingDuration { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Above this many live partitions on this store, warm state is released early for every partition, not only the excess ones.</summary>
    /// <remarks>Default: 10,000. Must be at least 1.</remarks>
    public int MaxWarmPartitions { get; init; } = 10_000;

    /// <summary>Scales the local budget until some limiter on this store first reaches it. 1.0 means no effect.</summary>
    /// <remarks>Default: 1.0. Must be greater than zero and at most one.</remarks>
    public double ColdStartFallbackFactor { get; init; } = 1.0;

    /// <summary>Decides, for exceptions thrown by the store limiter, whether one counts as a store failure. The library's own timeout and open-breaker exceptions always count as store failures, and caller cancellation never does, regardless of what this returns.</summary>
    /// <remarks>
    /// Default: <see langword="null"/>. When this is <see langword="null"/> (or it is set but does not classify a
    /// given exception), <see cref="ArgumentException"/>, <see cref="ObjectDisposedException"/>, and
    /// <see cref="InvalidOperationException"/> are treated as caller mistakes and reach the caller; every other
    /// exception is treated as a store failure and triggers the fallback path.
    /// </remarks>
    public Func<Exception, bool>? ShouldHandle { get; init; }

    /// <summary>Raised for the first failure of each exception type, and again once that type has been quiet for <see cref="BreakerSamplingDuration"/> or the breaker has closed since it last failed.</summary>
    /// <remarks>Default: <see langword="null"/>. This callback never throws on a request path: an exception raised from it is caught and ignored.</remarks>
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
