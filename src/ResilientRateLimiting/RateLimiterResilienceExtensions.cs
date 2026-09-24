using System.Threading.RateLimiting;

namespace ResilientRateLimiting;

/// <summary>Adds resilience to a rate limiter backed by a shared store.</summary>
public static class RateLimiterResilienceExtensions
{
    /// <summary>Wraps this limiter so a slow or unreachable store takes the configured fallback path instead of reaching the caller.</summary>
    /// <param name="primary">The limiter backed by the shared store.</param>
    /// <param name="fallback">In-memory fallback limiter, required unless the configured behaviour is fail-open or fail-closed.</param>
    /// <param name="options">Configuration.</param>
    /// <param name="storeHealth">One per store connection, shared by every partition — never one per partition.</param>
    /// <param name="timeProvider">Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <returns>A limiter that can be used anywhere the original could.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="primary"/>, <paramref name="options"/>, or <paramref name="storeHealth"/> is <see langword="null"/>; or <paramref name="fallback"/> is <see langword="null"/> while <paramref name="options"/> needs a local fallback.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="options"/> is incomplete or contradictory.</exception>
    /// <example>
    /// <code>
    /// RateLimiter limiter = storeLimiter.WithResilience(
    ///     fallback: new FixedWindowRateLimiter(fallbackOptions),
    ///     options: new ResilientRateLimiterOptions { FallbackRecoveryTime = TimeSpan.FromMinutes(1) },
    ///     storeHealth: storeHealth);
    /// </code>
    /// </example>
    public static RateLimiter WithResilience(
        this RateLimiter primary,
        RateLimiter? fallback,
        ResilientRateLimiterOptions options,
        StoreHealth storeHealth,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(storeHealth);

        return new ResilientRateLimiter(primary, fallback, options, storeHealth, timeProvider);
    }
}
