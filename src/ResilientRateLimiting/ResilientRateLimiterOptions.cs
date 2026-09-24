namespace ResilientRateLimiting;

/// <summary>Configuration for one <see cref="ResilientRateLimiter"/>. Settings shared by every limiter on a store connection live on <see cref="StoreHealthOptions"/>.</summary>
public sealed record ResilientRateLimiterOptions
{
    /// <summary>The largest accepted <see cref="FallbackRecoveryTime"/>: one day.</summary>
    public static readonly TimeSpan MaxFallbackRecoveryTime = TimeSpan.FromDays(1);

    /// <summary>What to do when the store cannot answer.</summary>
    /// <remarks>Default: <see cref="StoreFailureBehavior.LocalFallback"/>.</remarks>
    public StoreFailureBehavior FailureBehavior { get; init; } = StoreFailureBehavior.LocalFallback;

    /// <summary>How long the fallback limiter takes to refill completely. Required; a limiter cannot be asked.</summary>
    /// <remarks>
    /// Required and must be greater than zero when <see cref="FailureBehavior"/> is
    /// <see cref="StoreFailureBehavior.LocalFallback"/>. Must not exceed <see cref="MaxFallbackRecoveryTime"/>
    /// (one day) no matter which <see cref="FailureBehavior"/> is chosen, because the degraded Retry-After
    /// estimate uses this value on every failure behaviour, not only local fallback.
    /// </remarks>
    public TimeSpan FallbackRecoveryTime { get; init; }

    /// <summary>Upper bound on how long warm fallback state is held for an idle partition.</summary>
    /// <remarks>Default: two minutes. Must be greater than zero.</remarks>
    public TimeSpan MaxWarmRetention { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Tag metrics with the partition key. One time series per key — unsafe for IP addresses.</summary>
    /// <remarks>Default: <see langword="false"/>. A common mistake is enabling this for a partition key with unbounded values (such as an IP address or a user id), which creates one metric time series per value and never stops growing.</remarks>
    public bool TagMetricsByPartitionKey { get; init; }

    /// <summary>The partition key, used as a metric tag only when <see cref="TagMetricsByPartitionKey"/> is set.</summary>
    /// <remarks>Default: <see langword="null"/>. Set automatically by <see cref="ResilientRateLimitPartition.Get{TKey}"/> when <see cref="TagMetricsByPartitionKey"/> is set and this is still <see langword="null"/>.</remarks>
    public string? PartitionKey { get; init; }

    /// <summary>Low-cardinality metric tag identifying this policy.</summary>
    /// <remarks>Default: <c>"default"</c>. Use one short, fixed name per policy (for example, the endpoint group it protects), not a value that varies per request.</remarks>
    public string PolicyName { get; init; } = "default";

    /// <summary>The most time added to the Retry-After hint: a random spread so rejected callers do not return together, and a longer wait while the store is down. Zero adds nothing.</summary>
    /// <remarks>Default: 60 seconds. Must not be negative.</remarks>
    public TimeSpan MaxAddedRetryDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Throws when the configuration is incomplete or contradictory. Reports every violated rule, not just the first.</summary>
    /// <exception cref="InvalidOperationException">The configuration cannot be used. The message lists every violated rule, one per line.</exception>
    public void Validate()
    {
        List<string>? errors = null;

        if (MaxWarmRetention <= TimeSpan.Zero)
        {
            (errors ??= []).Add($"{nameof(MaxWarmRetention)} must be greater than zero.");
        }

        if (FailureBehavior == StoreFailureBehavior.LocalFallback && FallbackRecoveryTime <= TimeSpan.Zero)
        {
            (errors ??= []).Add(
                $"{nameof(FallbackRecoveryTime)} must be set when {nameof(FailureBehavior)} is " +
                $"{nameof(StoreFailureBehavior.LocalFallback)}. A RateLimiter cannot be asked how long " +
                "it takes to refill.");
        }

        if (FallbackRecoveryTime > MaxFallbackRecoveryTime)
        {
            (errors ??= []).Add(
                $"{nameof(FallbackRecoveryTime)} must be at most 1 day. It is how long the fallback limiter " +
                "takes to refill completely; a larger value usually means a unit mistake.");
        }

        if (MaxAddedRetryDelay < TimeSpan.Zero)
        {
            (errors ??= []).Add($"{nameof(MaxAddedRetryDelay)} must not be negative.");
        }

        if (errors is not null)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }
    }
}
