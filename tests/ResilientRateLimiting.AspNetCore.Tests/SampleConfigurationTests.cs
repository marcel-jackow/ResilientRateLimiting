using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ResilientRateLimiting.AspNetCore.Tests;

public class SampleConfigurationTests
{
    [Fact]
    public void The_documented_sample_configuration_binds_every_store_setting()
    {
        var root = FindRepoRoot();
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(root, "samples", "ResilientRateLimiting.Samples.Web", "appsettings.json"))
            .Build();
        var services = new ServiceCollection();
        services.AddResilientRateLimiting(configuration.GetSection("ResilientRateLimiting:Store"));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<StoreHealthOptions>>().Value;

        Assert.Equal(TimeSpan.FromMilliseconds(20), options.StoreTimeout);
        Assert.Equal(5, options.FailuresBeforeOpen);
        Assert.Equal(0.5, options.FailureRatio);
        Assert.Equal(TimeSpan.FromSeconds(5), options.BreakDuration);
        Assert.Equal(TimeSpan.FromSeconds(10), options.BreakerSamplingDuration);
        Assert.Equal(10_000, options.MaxWarmPartitions);
        Assert.Equal(1.0, options.ColdStartFallbackFactor);
        Assert.NotNull(provider.GetRequiredService<StoreHealth>());

        // The sample's values equal the defaults, so a misspelled key would still pass the checks above.
        configuration.GetSection("ResilientRateLimiting:Store")
            .Get<StoreHealthOptions>(binder => binder.ErrorOnUnknownConfiguration = true);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ResilientRateLimiting.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
