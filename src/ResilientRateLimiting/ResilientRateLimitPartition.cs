using System.Threading.RateLimiting;

namespace ResilientRateLimiting;

/// <summary>Builds partitions whose limiters are already wrapped for resilience.</summary>
public static class ResilientRateLimitPartition
{
    /// <summary>Creates a partition whose limiter falls back locally when the store fails.</summary>
    /// <typeparam name="TKey">The partition key type.</typeparam>
    /// <param name="partitionKey">The key this partition counts for.</param>
    /// <param name="primaryFactory">Builds the store-backed limiter for a key.</param>
    /// <param name="fallbackFactory">Builds a key's own in-memory fallback limiter, sized with <see cref="LocalBudget.ForReplicas"/>, not a literal.</param>
    /// <param name="options">Configuration.</param>
    /// <param name="storeHealth">One per store connection, shared by every partition — never one per partition.</param>
    /// <param name="timeProvider">Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <returns>A partition usable with <c>PartitionedRateLimiter.Create</c> or an ASP.NET Core policy.</returns>
    public static RateLimitPartition<TKey> Get<TKey>(
        TKey partitionKey,
        Func<TKey, RateLimiter> primaryFactory,
        Func<TKey, RateLimiter>? fallbackFactory,
        ResilientRateLimiterOptions options,
        StoreHealth storeHealth,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(primaryFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(storeHealth);

        return RateLimitPartition.Get(
            partitionKey,
            key =>
            {
                var partitionOptions = options.TagMetricsByPartitionKey && options.PartitionKey is null
                    ? options with { PartitionKey = key?.ToString() }
                    : options;

                return new ResilientRateLimiter(
                    primaryFactory(key),
                    fallbackFactory?.Invoke(key),
                    partitionOptions,
                    storeHealth,
                    timeProvider);
            });
    }
}
