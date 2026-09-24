using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ResilientRateLimiting.Tests;
using System.Net;
using System.Threading.RateLimiting;
using Xunit;

namespace ResilientRateLimiting.AspNetCore.Tests;

public class MiddlewareTests
{
    [Fact]
    public async Task A_limited_request_comes_back_as_429_with_a_retry_after_header()
    {
        var storeHealth = new StoreHealth(new StoreHealthOptions());

        using var host = await new HostBuilder()
            .ConfigureWebHost(builder =>
            {
                builder.UseTestServer();
                builder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddRateLimiter(options =>
                    {
                        options.UseResilientDefaults();
                        options.AddPolicy("test", _ => ResilientRateLimitPartition.Get(
                            "one-partition",
                            primaryFactory: _ => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = 1,
                                Window = TimeSpan.FromMinutes(5),
                                QueueLimit = 0,
                            }),
                            fallbackFactory: _ => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = 1,
                                Window = TimeSpan.FromMinutes(5),
                                QueueLimit = 0,
                            }),
                            new ResilientRateLimiterOptions
                            {
                                FallbackRecoveryTime = TimeSpan.FromMinutes(5),
                            },
                            storeHealth));
                    });
                });
                builder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints =>
                        endpoints.MapGet("/", () => "ok").RequireRateLimiting("test"));
                });
            })
            .StartAsync(TestContext.Current.CancellationToken);

        using var client = host.GetTestClient();

        var first = await client.GetAsync("/", TestContext.Current.CancellationToken);
        var second = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.NotNull(second.Headers.RetryAfter);
    }

    [Fact]
    public async Task An_outage_falls_back_locally_and_still_returns_429_with_a_retry_after_header()
    {
        var storeHealth = new StoreHealth(new StoreHealthOptions());

        using var host = await new HostBuilder()
            .ConfigureWebHost(builder =>
            {
                builder.UseTestServer();
                builder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddRateLimiter(options =>
                    {
                        options.UseResilientDefaults();
                        options.AddPolicy("test", _ => ResilientRateLimitPartition.Get(
                            "one-partition",
                            primaryFactory: _ => new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down")),
                            fallbackFactory: _ => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = 1,
                                Window = TimeSpan.FromMinutes(5),
                                QueueLimit = 0,
                            }),
                            new ResilientRateLimiterOptions
                            {
                                FallbackRecoveryTime = TimeSpan.FromMinutes(5),
                            },
                            storeHealth));
                    });
                });
                builder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints =>
                        endpoints.MapGet("/", () => "ok").RequireRateLimiting("test"));
                });
            })
            .StartAsync(TestContext.Current.CancellationToken);

        using var client = host.GetTestClient();

        var first = await client.GetAsync("/", TestContext.Current.CancellationToken);
        var second = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.NotNull(second.Headers.RetryAfter);
    }
}
