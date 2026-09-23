namespace ResilientRateLimiting;

/// <summary>Configuration for one <see cref="ResilientRateLimiter"/>. Settings shared by every limiter on a store connection live on <see cref="StoreHealthOptions"/>.</summary>
public sealed record ResilientRateLimiterOptions
{
    /// <summary>What to do when the store cannot answer.</summary>
    public StoreFailureBehavior FailureBehavior { get; init; } = StoreFailureBehavior.LocalFallback;

    /// <summary>How long the fallback limiter takes to refill completely. Required; a limiter cannot be asked.</summary>
    public TimeSpan FallbackRecoveryTime { get; init; }

    /// <summary>Upper bound on how long warm fallback state is held for an idle partition.</summary>
    public TimeSpan MaxWarmRetention { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Tag metrics with the partition key. One time series per key — unsafe for IP addresses.</summary>
    public bool TagMetricsByPartitionKey { get; init; }

    /// <summary>The partition key, used as a metric tag only when <see cref="TagMetricsByPartitionKey"/> is set.</summary>
    public string? PartitionKey { get; init; }

    /// <summary>Low-cardinality metric tag identifying this policy.</summary>
    public string PolicyName { get; init; } = "default";

    /// <summary>Throws when the configuration is incomplete or contradictory.</summary>
    /// <exception cref="InvalidOperationException">The configuration cannot be used.</exception>
    public void Validate()
    {
        if (MaxWarmRetention <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(MaxWarmRetention)} must be greater than zero.");
        }

        if (FailureBehavior != StoreFailureBehavior.LocalFallback)
        {
            return;
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
