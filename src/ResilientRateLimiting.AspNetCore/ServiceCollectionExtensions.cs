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
    /// <param name="configure">Applied to the bound options before logging is attached, for the settings configuration cannot bind, such as ShouldHandle and OnStoreFailure; the result is validated when the <see cref="StoreHealth"/> is created.</param>
    /// <returns>The same <paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="storeHealthSection"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">This method was already called once on <paramref name="services"/>.</exception>
    /// <remarks>
    /// <para>The options are bound from configuration first, then <paramref name="configure"/> runs on the bound
    /// result, and logging is attached last — so a hand-set <see cref="StoreHealthOptions.OnStoreFailure"/> from
    /// <paramref name="configure"/> still runs, in addition to the log line, because
    /// <see cref="StoreHealthOptionsExtensions.WithLogging"/> keeps any existing callback rather than replacing it.
    /// The bound options are validated (<see cref="StoreHealthOptions.Validate"/>) at application startup through
    /// <c>ValidateOnStart</c>, which only sees the values bound from <paramref name="storeHealthSection"/>. The
    /// version <paramref name="configure"/> produces is validated separately, inside the <see cref="StoreHealth"/>
    /// constructor, which runs when the singleton is first resolved — normally the first request that hits a policy
    /// using it, not necessarily at startup. A <paramref name="configure"/> that makes the options invalid is
    /// therefore only caught at that first resolution.</para>
    /// <para>The <see cref="StoreHealth"/> is logged under the category <c>"ResilientRateLimiting.StoreHealth"</c>.
    /// If a <see cref="TimeProvider"/> is already registered in <paramref name="services"/>, that instance is used;
    /// otherwise <see cref="TimeProvider.System"/> is used.</para>
    /// <para>This method registers only the store connection's health, not a limiter's options: build
    /// <see cref="ResilientRateLimiterOptions"/> once at startup and close over them in each policy lambda, rather
    /// than resolving them from <paramref name="services"/> — they are never registered here.</para>
    /// <para>A second store connection needs its own <see cref="StoreHealth"/>, built by hand
    /// (<c>new StoreHealth(options.WithLogging(logger))</c>) rather than by calling this method again. Do not also
    /// register that hand-built instance as another unkeyed <see cref="StoreHealth"/> service: dependency injection
    /// resolves an unkeyed service to whichever registration ran last, so
    /// <c>GetRequiredService&lt;StoreHealth&gt;()</c> would then return the second store's health to every policy,
    /// including ones meant to use the first. Instead, capture the hand-built instance in a local variable and use
    /// it directly inside the one policy that needs it.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddResilientRateLimiting(
    ///     builder.Configuration.GetSection("ResilientRateLimiting:Store"),
    ///     configure: options => options with { ShouldHandle = ex => ex is RedisConnectionException });
    /// </code>
    /// </example>
    public static IServiceCollection AddResilientRateLimiting(
        this IServiceCollection services,
        IConfiguration storeHealthSection,
        Func<StoreHealthOptions, StoreHealthOptions>? configure = null)
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
            var logger = loggerFactory.CreateLogger("ResilientRateLimiting.StoreHealth");
            var timeProvider = provider.GetService<TimeProvider>() ?? TimeProvider.System;

            var configured = configure is null ? options : configure(options);

            return new StoreHealth(configured.WithLogging(logger), timeProvider);
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
