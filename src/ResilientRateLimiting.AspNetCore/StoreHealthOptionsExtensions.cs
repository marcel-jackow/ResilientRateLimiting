using Microsoft.Extensions.Logging;

namespace ResilientRateLimiting.AspNetCore;

/// <summary>Adds logging to a <see cref="StoreHealthOptions"/> configuration.</summary>
public static class StoreHealthOptionsExtensions
{
    private const string MessageTemplate = "Store call failed: {ExceptionType}";

    /// <summary>
    /// Returns a copy that logs each reported store failure at <see cref="LogLevel.Warning"/> in addition to
    /// any <see cref="StoreHealthOptions.OnStoreFailure"/> already set on <paramref name="options"/>.
    /// </summary>
    /// <param name="options">The configuration to add logging to.</param>
    /// <param name="logger">Receives a Warning each time the shared <see cref="StoreHealth"/> reports a failure: the first of each exception type, and again once that type has been quiet for <see cref="StoreHealthOptions.BreakerSamplingDuration"/> or the breaker has closed since it last failed.</param>
    /// <returns>A new <see cref="StoreHealthOptions"/> with the combined callback. <paramref name="options"/> itself is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="logger"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>Call this once per <see cref="StoreHealthOptions"/> instance, right before it is passed to
    /// <see cref="StoreHealth"/>'s constructor (that is what <see cref="ServiceCollectionExtensions.AddResilientRateLimiting"/>
    /// does for its store connection). Calling it twice on the same options wraps the callback twice, so each
    /// failure would be logged twice.</para>
    /// <para>The message text names only the exception's type; the exception itself is passed to the logger too, so
    /// a logging provider that renders exception details (message, stack trace) will still show whatever the
    /// exception carries.</para>
    /// </remarks>
    public static StoreHealthOptions WithLogging(this StoreHealthOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var existing = options.OnStoreFailure;

        return options with
        {
            OnStoreFailure = exception =>
            {
                logger.LogWarning(exception, MessageTemplate, exception.GetType().Name);
                existing?.Invoke(exception);
            },
        };
    }
}
