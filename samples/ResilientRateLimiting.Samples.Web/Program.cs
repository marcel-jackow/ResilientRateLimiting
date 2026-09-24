using Microsoft.AspNetCore.RateLimiting;
using OpenTelemetry.Metrics;
using RedisRateLimiting;
using ResilientRateLimiting;
using ResilientRateLimiting.AspNetCore;
using StackExchange.Redis;
using System.Net;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

var rateLimits = builder.Configuration.GetSection("RateLimits").Get<RateLimitsOptions>()
    ?? throw new InvalidOperationException("The RateLimits configuration section is missing.");

var redisConnectionString = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("The ConnectionStrings:Redis configuration value is missing.");

var redisOptions = ConfigurationOptions.Parse(redisConnectionString);
redisOptions.AsyncTimeout = 20; // milliseconds; stays at or below StoreTimeout so a slow store never blocks a request past the configured limit.

var redis = await ConnectionMultiplexer.ConnectAsync(redisOptions);

// snippet: web-configure
StoreHealthOptions Configure(StoreHealthOptions options) => options with
{
    ShouldHandle = exception => exception is RedisException or TimeoutException,
    OnStoreFailure = exception => Console.WriteLine($"Store failure: {exception.GetType().Name}"),
};
// end-snippet

// snippet: web-services
builder.Services.AddSingleton(redis);
builder.Services.AddResilientRateLimiting(
    builder.Configuration.GetSection("ResilientRateLimiting:Store"),
    Configure);
// end-snippet

// snippet: web-with-logging
// A second store connection: built by hand, so its StoreHealth is built by hand too, with logging attached manually.
var secondaryRedisOptions = ConfigurationOptions.Parse(redisConnectionString);
secondaryRedisOptions.AsyncTimeout = 20;

var secondaryRedis = await ConnectionMultiplexer.ConnectAsync(secondaryRedisOptions);
var secondaryLogger = LoggerFactory.Create(logging => logging.AddConsole())
    .CreateLogger("ResilientRateLimiting.SecondStore");

var secondaryStoreHealth = new StoreHealth(new StoreHealthOptions().WithLogging(secondaryLogger));
// end-snippet

builder.Services.AddSingleton(secondaryRedis);
builder.Services.AddSingleton(secondaryStoreHealth);

var policyOptions = new ResilientRateLimiterOptions
{
    PolicyName = "per-client",
    FallbackRecoveryTime = TimeSpan.FromSeconds(rateLimits.WindowSeconds),
};

var localPermitLimit = LocalBudget.ForReplicas(
    sharedPermitLimit: rateLimits.PermitLimit,
    replicaCount: rateLimits.TypicalReplicaCount);

builder.Services.AddRateLimiter(limiterOptions =>
{
    // snippet: web-degraded-header
    limiterOptions.UseResilientDefaults(emitDegradedHeader: context =>
        context.Connection.RemoteIpAddress is { } remoteIp && IPAddress.IsLoopback(remoteIp));
    // end-snippet

    // snippet: web-policy
    limiterOptions.AddPolicy("per-client", context =>
    {
        var clientId = context.Request.Headers["X-Client-Id"].FirstOrDefault() ?? "anonymous";
        var storeHealth = context.RequestServices.GetRequiredService<StoreHealth>();

        return ResilientRateLimitPartition.Get(
            clientId,
            key => new RedisSlidingWindowRateLimiter<string>(key, new RedisSlidingWindowRateLimiterOptions
            {
                PermitLimit = rateLimits.PermitLimit,
                Window = TimeSpan.FromSeconds(rateLimits.WindowSeconds),
                ConnectionMultiplexerFactory = () => redis,
            }),
            key => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = localPermitLimit,
                Window = TimeSpan.FromSeconds(rateLimits.WindowSeconds),
                QueueLimit = 0,
            }),
            policyOptions,
            storeHealth);
    });
    // end-snippet
});

// snippet: web-meter
builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter(ResilientRateLimiter.MeterName));
// end-snippet

var app = builder.Build();

app.UseRateLimiter();

app.MapGet("/", () => "hello").RequireRateLimiting("per-client");

app.Run();

internal sealed record RateLimitsOptions
{
    public int PermitLimit { get; init; }

    public int WindowSeconds { get; init; }

    public int TypicalReplicaCount { get; init; }
}
