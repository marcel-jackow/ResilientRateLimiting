using RedisRateLimiting;
using StackExchange.Redis;
using System.Threading.RateLimiting;
using Testcontainers.Redis;
using Xunit;

namespace ResilientRateLimiting.IntegrationTests;

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>A connection of its own per simulated replica.</summary>
    public Task<ConnectionMultiplexer> ConnectAsync() => ConnectionMultiplexer.ConnectAsync(ConnectionString);
}

[CollectionDefinition(nameof(RedisCollection))]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>;

internal static class Replica
{
    public static ResilientRateLimiterOptions Options() => new()
    {
        ExpectedReplicaCount = 3,
        FallbackRecoveryTime = TimeSpan.FromMinutes(1),
        StoreTimeout = TimeSpan.FromSeconds(2),
    };

    public static ResilientRateLimiter Build(ConnectionMultiplexer connection, string partitionKey, int permitLimit) =>
        new(
            primary: new RedisSlidingWindowRateLimiter<string>(partitionKey, new RedisSlidingWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(1),
                ConnectionMultiplexerFactory = () => connection,
            }),
            fallback: new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }),
            options: Options());
}
