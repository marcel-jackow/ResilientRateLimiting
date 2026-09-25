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

// snippet: web-async-timeout
var redisOptions = ConfigurationOptions.Parse(redisConnectionString);
redisOptions.AsyncTimeout = 20; // milliseconds; stays at or below StoreTimeout so a slow store never blocks a request past the configured limit.
// end-snippet

var redis = await ConnectionMultiplexer.ConnectAsync(redisOptions);

// snippet: web-configure
StoreHealthOptions Configure(StoreHealthOptions options) => options with
{
    // The library's own timeout and breaker always count as store failures, so this only names the store's own exceptions.
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
var secondaryRedisConnectionString = builder.Configuration.GetConnectionString("RedisSecondary")
    ?? throw new InvalidOperationException("The ConnectionStrings:RedisSecondary configuration value is missing.");

var secondaryRedisOptions = ConfigurationOptions.Parse(secondaryRedisConnectionString);
secondaryRedisOptions.AsyncTimeout = 20;

var secondaryRedis = await ConnectionMultiplexer.ConnectAsync(secondaryRedisOptions);
var secondaryLogger = LoggerFactory.Create(logging => logging.AddConsole())
    .CreateLogger("ResilientRateLimiting.SecondStore");

var secondaryStoreHealth = new StoreHealth(new StoreHealthOptions().WithLogging(secondaryLogger));
// end-snippet

var policyOptions = new ResilientRateLimiterOptions
{
    PolicyName = "per-client",
    FallbackRecoveryTime = TimeSpan.FromSeconds(rateLimits.WindowSeconds),
};

// The second connection's own policy, not registered in DI: see the comment above AddPolicy("per-client-secondary").
var secondaryPolicyOptions = policyOptions with { PolicyName = "per-client-secondary" };

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
        // Demo only: the caller controls headers. In production, use trusted data such as user claims.
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

    // The second store connection's StoreHealth is never registered in DI: a second unkeyed AddSingleton would
    // shadow the first one, because GetRequiredService<StoreHealth>() only ever returns the last registration.
    // It is captured by this lambda instead, the same way secondaryRedis is.
    limiterOptions.AddPolicy("per-client-secondary", context =>
    {
        // Demo only: the caller controls headers. In production, use trusted data such as user claims.
        var clientId = context.Request.Headers["X-Client-Id"].FirstOrDefault() ?? "anonymous";

        return ResilientRateLimitPartition.Get(
            clientId,
            key => new RedisSlidingWindowRateLimiter<string>(key, new RedisSlidingWindowRateLimiterOptions
            {
                PermitLimit = rateLimits.PermitLimit,
                Window = TimeSpan.FromSeconds(rateLimits.WindowSeconds),
                ConnectionMultiplexerFactory = () => secondaryRedis,
            }),
            key => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = localPermitLimit,
                Window = TimeSpan.FromSeconds(rateLimits.WindowSeconds),
                QueueLimit = 0,
            }),
            secondaryPolicyOptions,
            secondaryStoreHealth);
    });
});

// snippet: web-meter
builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter(ResilientRateLimiter.MeterName));
// end-snippet

var app = builder.Build();

app.UseRateLimiter();

app.MapGet("/", () => "hello").RequireRateLimiting("per-client");
app.MapGet("/secondary", () => "hello from secondary").RequireRateLimiting("per-client-secondary");

app.Run();

internal sealed record RateLimitsOptions
{
    public int PermitLimit { get; init; }

    public int WindowSeconds { get; init; }

    public int TypicalReplicaCount { get; init; }
}
