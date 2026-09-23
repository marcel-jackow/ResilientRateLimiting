using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ResilientRateLimiting.AspNetCore.Tests;

public class ServiceCollectionExtensionsTests
{
    private static IConfiguration ConfigurationFrom(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class CategoryCapturingLoggerFactory : ILoggerFactory
    {
        public string? Category { get; private set; }

        public ILogger CreateLogger(string categoryName)
        {
            Category = categoryName;
            return NullLogger.Instance;
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void StoreHealth_is_logged_under_the_store_health_category()
    {
        var configuration = ConfigurationFrom([]);
        var loggerFactory = new CategoryCapturingLoggerFactory();

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        services.AddResilientRateLimiting(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<StoreHealth>();

        Assert.Equal("ResilientRateLimiting.StoreHealth", loggerFactory.Category);
    }

    [Fact]
    public async Task An_invalid_configuration_section_fails_the_host_at_start_not_on_first_use()
    {
        var configuration = ConfigurationFrom(new Dictionary<string, string?>
        {
            ["StoreTimeout"] = "00:00:00",
        });

        using var host = new HostBuilder()
            .ConfigureWebHost(builder =>
            {
                builder.UseTestServer();
                builder.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddResilientRateLimiting(configuration);
                });
                builder.Configure(_ => { });
            })
            .Build();

        await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_valid_configuration_section_binds_into_the_resolved_store_health()
    {
        var configuration = ConfigurationFrom(new Dictionary<string, string?>
        {
            ["StoreTimeout"] = "00:00:00.0500000",
            ["FailuresBeforeOpen"] = "7",
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddResilientRateLimiting(configuration);

        using var provider = services.BuildServiceProvider();
        var storeHealth = provider.GetRequiredService<StoreHealth>();

        Assert.Equal(TimeSpan.FromMilliseconds(50), storeHealth.Options.StoreTimeout);
        Assert.Equal(7, storeHealth.Options.FailuresBeforeOpen);
    }

    [Fact]
    public void StoreHealth_resolves_to_the_same_instance_every_time()
    {
        var configuration = ConfigurationFrom([]);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddResilientRateLimiting(configuration);

        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<StoreHealth>();
        var second = provider.GetRequiredService<StoreHealth>();

        Assert.Same(first, second);
    }

    [Fact]
    public void A_second_call_throws_instead_of_merging_into_the_first_store_connection()
    {
        var configuration = ConfigurationFrom([]);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddResilientRateLimiting(configuration);

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddResilientRateLimiting(configuration));

        Assert.Contains("AddResilientRateLimiting", exception.Message);
        Assert.Contains("StoreHealth", exception.Message);
    }

    [Fact]
    public void Resolves_without_AddLogging_in_the_collection()
    {
        var configuration = ConfigurationFrom([]);

        var services = new ServiceCollection();
        services.AddResilientRateLimiting(configuration);

        using var provider = services.BuildServiceProvider();

        var storeHealth = provider.GetRequiredService<StoreHealth>();

        Assert.NotNull(storeHealth);
    }
}
