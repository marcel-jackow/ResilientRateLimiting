using Xunit;

namespace ResilientRateLimiting.Tests;

public class ResilientRateLimiterOptionsTests
{
    private static ResilientRateLimiterOptions Valid() => new()
    {
        FallbackRecoveryTime = TimeSpan.FromMinutes(1),
    };

    [Fact]
    public void Accepts_a_fully_specified_configuration()
    {
        Valid().Validate();
    }

    [Fact]
    public void Rejects_an_unset_fallback_recovery_time()
    {
        var options = new ResilientRateLimiterOptions { FallbackRecoveryTime = TimeSpan.Zero };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(ResilientRateLimiterOptions.FallbackRecoveryTime), error.Message);
    }

    [Fact]
    public void Does_not_require_fallback_settings_when_the_fallback_is_not_used()
    {
        new ResilientRateLimiterOptions { FailureBehavior = StoreFailureBehavior.FailOpen }.Validate();
    }

    [Fact]
    public void Rejects_a_non_positive_max_warm_retention()
    {
        var options = new ResilientRateLimiterOptions
        {
            FallbackRecoveryTime = TimeSpan.FromMinutes(1),
            MaxWarmRetention = TimeSpan.Zero,
        };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(ResilientRateLimiterOptions.MaxWarmRetention), error.Message);
    }

    [Fact]
    public void Reports_every_violated_rule_at_once()
    {
        var options = new ResilientRateLimiterOptions
        {
            FallbackRecoveryTime = TimeSpan.Zero,
            MaxWarmRetention = TimeSpan.Zero,
        };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(ResilientRateLimiterOptions.FallbackRecoveryTime), error.Message);
        Assert.Contains(nameof(ResilientRateLimiterOptions.MaxWarmRetention), error.Message);
    }

    [Fact]
    public void Rejects_a_negative_max_added_retry_delay()
    {
        var options = Valid() with { MaxAddedRetryDelay = TimeSpan.FromSeconds(-1) };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(ResilientRateLimiterOptions.MaxAddedRetryDelay), error.Message);
    }

    [Fact]
    public void Accepts_a_zero_max_added_retry_delay()
    {
        (Valid() with { MaxAddedRetryDelay = TimeSpan.Zero }).Validate();
    }

    [Fact]
    public void Reports_a_negative_max_added_retry_delay_alongside_another_violated_rule()
    {
        var options = new ResilientRateLimiterOptions
        {
            FallbackRecoveryTime = TimeSpan.FromMinutes(1),
            MaxWarmRetention = TimeSpan.Zero,
            MaxAddedRetryDelay = TimeSpan.FromSeconds(-1),
        };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(ResilientRateLimiterOptions.MaxWarmRetention), error.Message);
        Assert.Contains(nameof(ResilientRateLimiterOptions.MaxAddedRetryDelay), error.Message);
    }
}
