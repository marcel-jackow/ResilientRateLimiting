using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ResilientRateLimiting.AspNetCore;

/// <summary>Wires one shared store connection's health into dependency injection.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="StoreHealthOptions"/> from <paramref name="storeHealthSection"/>, validates them at
    /// startup, and registers one singleton <see cref="StoreHealth"/> for this store connection with logging
    /// attached. Resolve it in a policy lambda via <c>context.RequestServices.GetRequiredService&lt;StoreHealth&gt;()</c>.
    /// Call this once per application; for a second store connection, build a further <see cref="StoreHealth"/> by hand.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="storeHealthSection">The configuration section for this store connection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddResilientRateLimiting(
        this IServiceCollection services,
        IConfiguration storeHealthSection)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(storeHealthSection);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(StoreHealth)))
        {
            throw new InvalidOperationException(
                "AddResilientRateLimiting was already called on this service collection. It registers one store " +
                "connection per application; for a second store connection, build a further StoreHealth instance " +
                "by hand instead of calling this method again.");
        }

        services.AddOptions<StoreHealthOptions>().Bind(storeHealthSection).ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<StoreHealthOptions>, ValidateStoreHealthOptions>());

        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<StoreHealthOptions>>().Value;
            var loggerFactory = provider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
            var logger = loggerFactory.CreateLogger("ResilientRateLimiting");
            var timeProvider = provider.GetService<TimeProvider>() ?? TimeProvider.System;

            return new StoreHealth(options.WithLogging(logger), timeProvider);
        });

        return services;
    }
}

/// <summary>Adapts <see cref="StoreHealthOptions.Validate"/> to the options-validation pipeline.</summary>
internal sealed class ValidateStoreHealthOptions : IValidateOptions<StoreHealthOptions>
{
    /// <summary>Runs <see cref="StoreHealthOptions.Validate"/> and turns a failure into a <see cref="ValidateOptionsResult"/>.</summary>
    public ValidateOptionsResult Validate(string? name, StoreHealthOptions options)
    {
        try
        {
            options.Validate();
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException exception)
        {
            return ValidateOptionsResult.Fail(exception.Message);
        }
    }
}
