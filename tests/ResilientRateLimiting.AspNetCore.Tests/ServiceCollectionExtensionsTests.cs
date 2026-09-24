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

    [Fact]
    public void The_configure_delegate_s_ShouldHandle_is_honoured_by_the_resolved_store_health()
    {
        // Without ShouldHandle, the default classifier treats InvalidOperationException as a
        // programming error (not a store failure): a custom ShouldHandle must be able to override that.
        var configuration = ConfigurationFrom([]);

        var services = new ServiceCollection();
        services.AddResilientRateLimiting(configuration, o => o with { ShouldHandle = exception => exception is InvalidOperationException });

        using var provider = services.BuildServiceProvider();
        var storeHealth = provider.GetRequiredService<StoreHealth>();

        Assert.True(storeHealth.IsStoreFailure(new InvalidOperationException()));
        Assert.False(storeHealth.IsStoreFailure(new IOException()));
    }

    [Fact]
    public void The_configure_delegate_s_OnStoreFailure_still_runs_alongside_the_attached_logging()
    {
        var configuration = ConfigurationFrom([]);
        var reported = new List<Exception>();
        var loggerProvider = new CapturingLoggerProvider();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(loggerProvider));
        services.AddResilientRateLimiting(configuration, o => o with { OnStoreFailure = reported.Add });

        using var provider = services.BuildServiceProvider();
        var storeHealth = provider.GetRequiredService<StoreHealth>();

        storeHealth.ReportFirstOccurrence(new InvalidDataException("store down"));

        Assert.Single(reported);
        Assert.Single(loggerProvider.Warnings);
    }

    [Fact]
    public void An_omitted_configure_delegate_behaves_as_before()
    {
        var configuration = ConfigurationFrom([]);

        var services = new ServiceCollection();
        services.AddResilientRateLimiting(configuration);

        using var provider = services.BuildServiceProvider();
        var storeHealth = provider.GetRequiredService<StoreHealth>();

        // The default classifier, unchanged: InvalidOperationException is a programming error, not a store failure.
        Assert.False(storeHealth.IsStoreFailure(new InvalidOperationException()));
    }

    /// <summary>Captures Warning-level messages logged through it, without needing a real sink.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Warnings { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                {
                    owner.Warnings.Add(formatter(state, exception));
                }
            }
        }
    }
}
