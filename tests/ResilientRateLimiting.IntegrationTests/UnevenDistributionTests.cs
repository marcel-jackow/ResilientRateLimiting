using StackExchange.Redis;
using System.Threading.RateLimiting;
using Xunit;

namespace ResilientRateLimiting.IntegrationTests;

[Collection(nameof(RedisCollection))]
public class UnevenDistributionTests(RedisFixture redis)
{
    [Theory]
    [InlineData(60, 30, 10)]
    [InlineData(90, 5, 5)]
    [InlineData(34, 33, 33)]
    public async Task The_shared_limit_holds_whatever_the_split(int first, int second, int third)
    {
        const int PermitLimit = 30;
        var partitionKey = $"uneven-{Guid.NewGuid():N}";
        var split = new[] { first, second, third };

        var connections = new List<ConnectionMultiplexer>();
        var limiters = new List<RateLimiter>();

        for (var replica = 0; replica < 3; replica++)
        {
            var connection = await redis.ConnectAsync();
            connections.Add(connection);
            limiters.Add(Replica.Build(connection, partitionKey, PermitLimit));
        }

        var admitted = 0;

        for (var replica = 0; replica < 3; replica++)
        {
            for (var request = 0; request < split[replica]; request++)
            {
                using var lease = await limiters[replica].AcquireAsync(1, TestContext.Current.CancellationToken);

                if (lease.IsAcquired)
                {
                    admitted++;
                }
            }
        }

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
