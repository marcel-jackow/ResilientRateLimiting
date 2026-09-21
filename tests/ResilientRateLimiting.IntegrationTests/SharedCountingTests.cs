using StackExchange.Redis;
using System.Threading.RateLimiting;
using Xunit;

namespace ResilientRateLimiting.IntegrationTests;

[Collection(nameof(RedisCollection))]
public class SharedCountingTests(RedisFixture redis)
{
    [Fact]
    public async Task Three_replicas_admit_the_shared_limit_not_three_times_it()
    {
        const int PermitLimit = 30;
        const int RequestCount = 90;
        var partitionKey = $"shared-{Guid.NewGuid():N}";

        var connections = new List<ConnectionMultiplexer>();
        var limiters = new List<RateLimiter>();

        for (var replica = 0; replica < 3; replica++)
        {
            var connection = await redis.ConnectAsync();
            connections.Add(connection);
            limiters.Add(Replica.Build(connection, partitionKey, PermitLimit));
        }

        var admitted = 0;

        for (var request = 0; request < RequestCount; request++)
        {
            using var lease = await limiters[request % 3].AcquireAsync(1, TestContext.Current.CancellationToken);

            if (lease.IsAcquired)
            {
                admitted++;
            }
        }

        // Without the shared counter these ninety requests admit ninety.
        Assert.Equal(PermitLimit, admitted);

        foreach (var limiter in limiters)
        {
            limiter.Dispose();
        }

        foreach (var connection in connections)
        {
            await connection.DisposeAsync();
        }
    }
}
