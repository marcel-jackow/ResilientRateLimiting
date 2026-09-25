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
    /// <exception cref="ArgumentNullException"><paramref name="primaryFactory"/>, <paramref name="options"/>, or <paramref name="storeHealth"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The wrapper for a key is built only when that key is first used, so these come later, from the first request
    /// for a key, not from this call: <see cref="ArgumentNullException"/> when <paramref name="fallbackFactory"/> is
    /// <see langword="null"/> or returns <see langword="null"/> while <paramref name="options"/> needs a local fallback;
    /// <see cref="ArgumentException"/> when it returns a <see cref="ConcurrencyLimiter"/>; and
    /// <see cref="InvalidOperationException"/> when <paramref name="options"/> fails
    /// <see cref="ResilientRateLimiterOptions.Validate"/>. To fail at startup instead, call <c>options.Validate()</c> when
    /// the app starts.
    /// </remarks>
    /// <example>
    /// <code>
    /// var limiter = PartitionedRateLimiter.Create&lt;HttpContext, string&gt;(context =>
    ///     ResilientRateLimitPartition.Get(
    ///         partitionKey: context.User.Identity?.Name ?? "anonymous",
    ///         primaryFactory: key => new RedisSlidingWindowRateLimiter&lt;string&gt;(key, storeWindowOptions),
    ///         fallbackFactory: key => new FixedWindowRateLimiter(fallbackOptions),
    ///         options: resilienceOptions,
    ///         storeHealth: storeHealth));
    /// </code>
    /// </example>
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
