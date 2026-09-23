using Microsoft.Extensions.Logging;

namespace ResilientRateLimiting.AspNetCore;

/// <summary>Adds logging to a <see cref="StoreHealthOptions"/> configuration.</summary>
public static class StoreHealthOptionsExtensions
{
    private const string MessageTemplate = "Store call failed: {ExceptionType}";

    /// <summary>
    /// Returns a copy that logs every store failure at <see cref="LogLevel.Warning"/> in addition to
    /// any <see cref="StoreHealthOptions.OnStoreFailure"/> already set on <paramref name="options"/>.
    /// </summary>
    /// <param name="options">The configuration to add logging to.</param>
    /// <param name="logger">Receives one Warning per distinct exception type reported by the shared <see cref="StoreHealth"/>.</param>
    /// <returns>A new <see cref="StoreHealthOptions"/> with the combined callback.</returns>
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
