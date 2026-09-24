using RedisRateLimiting;
using ResilientRateLimiting;
using StackExchange.Redis;
using System.Threading.RateLimiting;

/// <summary>Every scenario is self-contained so a documentation page can quote just its named region.</summary>
internal static class Scenarios
{
    // Runs only with --redis: needs Redis on localhost:6379.
    public static async Task QuickStart()
    {
        // snippet: quick-start
        var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        var storeHealth = new StoreHealth(new StoreHealthOptions());
        var options = new ResilientRateLimiterOptions { FallbackRecoveryTime = TimeSpan.FromMinutes(1) };

        var primary = new RedisSlidingWindowRateLimiter<string>("orders-api", new RedisSlidingWindowRateLimiterOptions
        {
            PermitLimit = 100,
            Window = TimeSpan.FromMinutes(1),
            ConnectionMultiplexerFactory = () => redis,
        });

        var fallback = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = LocalBudget.ForReplicas(sharedPermitLimit: 100, replicaCount: 3),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });

        using var limiter = primary.WithResilience(fallback, options, storeHealth);
        using var lease = await limiter.AcquireAsync(permitCount: 1);

        Console.WriteLine(lease.IsAcquired ? "Allowed" : "Rejected");
        // end-snippet
    }

    public static async Task StoreHealthScenario()
    {
        // snippet: store-health
        var storeHealth = new StoreHealth(new StoreHealthOptions
        {
            StoreTimeout = TimeSpan.FromMilliseconds(200),
            FailuresBeforeOpen = 5,
            BreakDuration = TimeSpan.FromSeconds(5),
        });
        // end-snippet

        using var primary = new InMemoryStore(permitLimit: 5, window: TimeSpan.FromSeconds(10));
        using var fallback = new InMemoryStore(permitLimit: 2, window: TimeSpan.FromSeconds(10));
        var options = new ResilientRateLimiterOptions { FallbackRecoveryTime = TimeSpan.FromMinutes(1) };
        using var limiter = primary.WithResilience(fallback, options, storeHealth);
        using var lease = await limiter.AcquireAsync(1);

        Console.WriteLine($"Allowed: {lease.IsAcquired}");
    }

    public static Task LocalBudgetScenario()
    {
        // snippet: local-budget
        var localLimit = LocalBudget.ForReplicas(sharedPermitLimit: 100, replicaCount: 3);

        using var fallback = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = localLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
        // end-snippet

        Console.WriteLine($"Local budget per replica: {localLimit}");

        return Task.CompletedTask;
    }

    public static async Task Constructor()
    {
        using var primary = new InMemoryStore(permitLimit: 5, window: TimeSpan.FromSeconds(10));
        using var fallback = new InMemoryStore(permitLimit: 2, window: TimeSpan.FromSeconds(10));
        var storeHealth = new StoreHealth(new StoreHealthOptions());
        var options = new ResilientRateLimiterOptions { FallbackRecoveryTime = TimeSpan.FromMinutes(1) };

        // snippet: constructor
        using var limiter = new ResilientRateLimiter(primary, fallback, options, storeHealth);
        // end-snippet

        using var lease = await limiter.AcquireAsync(1);
        Console.WriteLine($"Allowed: {lease.IsAcquired}");
    }

    public static async Task WithResilienceScenario()
    {
        using var primary = new InMemoryStore(permitLimit: 5, window: TimeSpan.FromSeconds(10));
        using var fallback = new InMemoryStore(permitLimit: 2, window: TimeSpan.FromSeconds(10));
        var storeHealth = new StoreHealth(new StoreHealthOptions());
        var options = new ResilientRateLimiterOptions { FallbackRecoveryTime = TimeSpan.FromMinutes(1) };

        // snippet: with-resilience
        using var limiter = primary.WithResilience(fallback, options, storeHealth);
        // end-snippet

        using var lease = await limiter.AcquireAsync(1);
        Console.WriteLine($"Allowed: {lease.IsAcquired}");
    }

    public static async Task Partitioned()
    {
        var storeHealth = new StoreHealth(new StoreHealthOptions());
        var options = new ResilientRateLimiterOptions { FallbackRecoveryTime = TimeSpan.FromMinutes(1) };

        // snippet: partitioned
        using var limiter = PartitionedRateLimiter.Create<string, string>(key =>
            ResilientRateLimitPartition.Get(
                key,
                partitionKey => new InMemoryStore(permitLimit: 5, window: TimeSpan.FromSeconds(10)),
                partitionKey => new InMemoryStore(permitLimit: 2, window: TimeSpan.FromSeconds(10)),
                options,
                storeHealth));
        // end-snippet

        using var lease = await limiter.AcquireAsync("tenant-a", 1);
        Console.WriteLine($"Allowed: {lease.IsAcquired}");
    }

    public static async Task PartitionMetricsTag()
    {
        var storeHealth = new StoreHealth(new StoreHealthOptions());

        // snippet: partition-metrics-tag
        var options = new ResilientRateLimiterOptions
        {
            FallbackRecoveryTime = TimeSpan.FromMinutes(1),
            TagMetricsByPartitionKey = true,
        };

        using var limiter = PartitionedRateLimiter.Create<string, string>(key =>
            ResilientRateLimitPartition.Get(
                key,
                partitionKey => new InMemoryStore(permitLimit: 5, window: TimeSpan.FromSeconds(10)),
                partitionKey => new InMemoryStore(permitLimit: 2, window: TimeSpan.FromSeconds(10)),
                options,
                storeHealth));
        // end-snippet

        using var lease = await limiter.AcquireAsync("tenant-a", 1);
        Console.WriteLine($"Allowed: {lease.IsAcquired}");
    }

    public static async Task FailOpen()
    {
        using var primary = new UnreachableStore();
        var storeHealth = new StoreHealth(new StoreHealthOptions());

        // snippet: fail-open
        var options = new ResilientRateLimiterOptions { FailureBehavior = StoreFailureBehavior.FailOpen };

        using var limiter = primary.WithResilience(fallback: null, options, storeHealth);
        // end-snippet

        using var lease = await limiter.AcquireAsync(1);
        Console.WriteLine($"Allowed: {lease.IsAcquired}");
    }

    public static async Task FailClosed()
    {
        using var primary = new UnreachableStore();
        var storeHealth = new StoreHealth(new StoreHealthOptions());

        // snippet: fail-closed
        var options = new ResilientRateLimiterOptions { FailureBehavior = StoreFailureBehavior.FailClosed };

        using var limiter = primary.WithResilience(fallback: null, options, storeHealth);
        // end-snippet

        using var lease = await limiter.AcquireAsync(1);
        Console.WriteLine($"Allowed: {lease.IsAcquired}");
    }

    public static async Task ReadSource()
    {
        using var primary = new InMemoryStore(permitLimit: 5, window: TimeSpan.FromSeconds(10));
        using var fallback = new InMemoryStore(permitLimit: 2, window: TimeSpan.FromSeconds(10));
        var storeHealth = new StoreHealth(new StoreHealthOptions());
        var options = new ResilientRateLimiterOptions { FallbackRecoveryTime = TimeSpan.FromMinutes(1) };
        using var limiter = primary.WithResilience(fallback, options, storeHealth);
        using var lease = await limiter.AcquireAsync(1);

        // snippet: read-source
        lease.TryGetMetadata(ResilientRateLimitLease.SourceMetadata, out var source);

        var description = source switch
        {
            LeaseSource.Distributed => "the shared store answered",
            LeaseSource.LocalFallback => "the store failed and the local fallback limiter answered",
            LeaseSource.FailOpen => "the store failed and the request was admitted",
            LeaseSource.FailClosed => "the store failed and the request was rejected",
            LeaseSource.Recovery => "the store just recovered and the local counter answered instead",
            _ => "unknown",
        };

        Console.WriteLine($"Answered by: {source} ({description})");
        // end-snippet
    }

    public static async Task ReadRetryAfter()
    {
        using var primary = new UnreachableStore();
        var storeHealth = new StoreHealth(new StoreHealthOptions());
        var options = new ResilientRateLimiterOptions { FailureBehavior = StoreFailureBehavior.FailClosed };
        using var limiter = primary.WithResilience(fallback: null, options, storeHealth);
        using var lease = await limiter.AcquireAsync(1);

        // snippet: read-retry-after
        var hasRetryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter);

        Console.WriteLine(hasRetryAfter ? $"Retry after: {retryAfter}" : "No Retry-After hint.");
        // end-snippet
    }
}
