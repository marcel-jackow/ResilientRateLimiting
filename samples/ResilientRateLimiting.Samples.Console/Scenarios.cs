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

        var primary = new InMemoryStore(permitLimit: 5, window: TimeSpan.FromSeconds(10));
        var fallback = new InMemoryStore(permitLimit: 2, window: TimeSpan.FromSeconds(10));
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
        var primary = new InMemoryStore(permitLimit: 5, window: TimeSpan.FromSeconds(10));
        var fallback = new InMemoryStore(permitLimit: 2, window: TimeSpan.FromSeconds(10));
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
        var primary = new InMemoryStore(permitLimit: 5, window: TimeSpan.FromSeconds(10));
        var fallback = new InMemoryStore(permitLimit: 2, window: TimeSpan.FromSeconds(10));
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
        var primary = new UnreachableStore();
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
        var primary = new UnreachableStore();
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
        var primary = new InMemoryStore(permitLimit: 5, window: TimeSpan.FromSeconds(10));
        var fallback = new InMemoryStore(permitLimit: 2, window: TimeSpan.FromSeconds(10));
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
        var primary = new UnreachableStore();
        var storeHealth = new StoreHealth(new StoreHealthOptions());
        var options = new ResilientRateLimiterOptions { FailureBehavior = StoreFailureBehavior.FailClosed };
        using var limiter = primary.WithResilience(fallback: null, options, storeHealth);
        using var lease = await limiter.AcquireAsync(1);

        // snippet: read-retry-after
        var hasRetryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter);

        Console.WriteLine(hasRetryAfter ? $"Retry after: {retryAfter}" : "No Retry-After hint.");
        // end-snippet
    }

    public static async Task StoreFailureCallback()
    {
        var primary = new UnreachableStore();
        var fallback = new InMemoryStore(permitLimit: 2, window: TimeSpan.FromSeconds(10));
        var options = new ResilientRateLimiterOptions { FallbackRecoveryTime = TimeSpan.FromMinutes(1) };

        // snippet: store-failure-callback
        var storeHealth = new StoreHealth(new StoreHealthOptions
        {
            OnStoreFailure = exception => Console.WriteLine($"Store failure: {exception.GetType().Name}"),
            ShouldHandle = exception => exception is IOException,
        });
        // end-snippet

        using var limiter = primary.WithResilience(fallback, options, storeHealth);
        using var lease = await limiter.AcquireAsync(1);
        Console.WriteLine($"Allowed: {lease.IsAcquired}");
    }

    public static Task OptionsCopy()
    {
        // snippet: options-copy
        var baseOptions = new ResilientRateLimiterOptions { FallbackRecoveryTime = TimeSpan.FromMinutes(1) };

        var ordersOptions = baseOptions with { PolicyName = "orders-api" };
        var paymentsOptions = baseOptions with { PolicyName = "payments-api" };
        // end-snippet

        Console.WriteLine($"Base: {baseOptions.PolicyName}, orders: {ordersOptions.PolicyName}, payments: {paymentsOptions.PolicyName}");

        return Task.CompletedTask;
    }

    public static Task Validate()
    {
        // snippet: validate
        var invalid = new ResilientRateLimiterOptions { MaxWarmRetention = TimeSpan.Zero };

        try
        {
            invalid.Validate();
        }
        catch (InvalidOperationException exception)
        {
            Console.WriteLine(exception.Message);
        }
        // end-snippet

        return Task.CompletedTask;
    }

    public static async Task TimeProviderScenario()
    {
        var primary = new InMemoryStore(permitLimit: 5, window: TimeSpan.FromSeconds(10));
        var fallback = new InMemoryStore(permitLimit: 2, window: TimeSpan.FromSeconds(10));
        var storeHealth = new StoreHealth(new StoreHealthOptions());
        var options = new ResilientRateLimiterOptions { FallbackRecoveryTime = TimeSpan.FromMinutes(1) };

        // snippet: time-provider
        var timeProvider = TimeProvider.System; // tests pass a fake TimeProvider instead

        using var limiter = primary.WithResilience(fallback, options, storeHealth, timeProvider);
        // end-snippet

        using var lease = await limiter.AcquireAsync(1);
        Console.WriteLine($"Allowed: {lease.IsAcquired}");
    }

    public static async Task AcquireAsyncNotAttempt()
    {
        var primary = new InMemoryStore(permitLimit: 5, window: TimeSpan.FromSeconds(10));
        var fallback = new InMemoryStore(permitLimit: 2, window: TimeSpan.FromSeconds(10));
        var storeHealth = new StoreHealth(new StoreHealthOptions());
        var options = new ResilientRateLimiterOptions { FallbackRecoveryTime = TimeSpan.FromMinutes(1) };
        using var limiter = primary.WithResilience(fallback, options, storeHealth);

        // snippet: acquire-async-not-attempt
        var attempted = limiter.AttemptAcquire(1);
        Console.WriteLine($"AttemptAcquire always rejects: {!attempted.IsAcquired}");

        using var lease = await limiter.AcquireAsync(1);
        Console.WriteLine($"AcquireAsync allowed: {lease.IsAcquired}");
        // end-snippet
    }

    public static async Task DisposeScenario()
    {
        var storeHealth = new StoreHealth(new StoreHealthOptions());
        var options = new ResilientRateLimiterOptions { FallbackRecoveryTime = TimeSpan.FromMinutes(1) };

        // snippet: dispose
        var primary = new InMemoryStore(permitLimit: 5, window: TimeSpan.FromSeconds(10));
        var fallback = new InMemoryStore(permitLimit: 2, window: TimeSpan.FromSeconds(10));

        // The wrapper disposes its primary and fallback, so only the wrapper needs disposing here.
        using var limiter = primary.WithResilience(fallback, options, storeHealth);
        // end-snippet

        using var lease = await limiter.AcquireAsync(1);
        Console.WriteLine($"Allowed: {lease.IsAcquired}");
    }

    public static async Task PermitCountCheck()
    {
        const int StorePermitLimit = 5;

        var primary = new InMemoryStore(permitLimit: StorePermitLimit, window: TimeSpan.FromSeconds(10));
        var fallback = new InMemoryStore(permitLimit: 2, window: TimeSpan.FromSeconds(10));
        var storeHealth = new StoreHealth(new StoreHealthOptions());
        var options = new ResilientRateLimiterOptions { FallbackRecoveryTime = TimeSpan.FromMinutes(1) };
        using var limiter = primary.WithResilience(fallback, options, storeHealth);

        var permitCount = 10;

        // snippet: permit-count-check
        // ArgumentOutOfRangeException here is a programming error, not a store failure, so check first instead of relying on it.
        if (permitCount > StorePermitLimit)
        {
            Console.WriteLine($"Rejected locally: {permitCount} exceeds the store limit of {StorePermitLimit}.");
        }
        else
        {
            using var lease = await limiter.AcquireAsync(permitCount);
            Console.WriteLine($"Allowed: {lease.IsAcquired}");
        }
        // end-snippet
    }

    public static async Task TokenBucketFallback()
    {
        var primary = new InMemoryStore(permitLimit: 100, window: TimeSpan.FromMinutes(1));
        var storeHealth = new StoreHealth(new StoreHealthOptions());

        // snippet: token-bucket-fallback
        var tokenLimit = 34;
        var tokensPerPeriod = 34;
        var replenishmentPeriod = TimeSpan.FromMinutes(1);

        // Rounded up: a bucket that is not exactly full still takes one more period to finish refilling.
        var fallbackRecoveryTime = Math.Ceiling(tokenLimit / (double)tokensPerPeriod) * replenishmentPeriod;

        var fallback = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = tokenLimit,
            TokensPerPeriod = tokensPerPeriod,
            ReplenishmentPeriod = replenishmentPeriod,
            QueueLimit = 0,
        });
        // end-snippet

        var options = new ResilientRateLimiterOptions { FallbackRecoveryTime = fallbackRecoveryTime };
        using var limiter = primary.WithResilience(fallback, options, storeHealth);
        using var lease = await limiter.AcquireAsync(1);
        Console.WriteLine($"Allowed: {lease.IsAcquired}, fallback recovery time: {fallbackRecoveryTime}");
    }

    public static async Task SlidingWindowFallback()
    {
        var primary = new InMemoryStore(permitLimit: 100, window: TimeSpan.FromMinutes(1));
        var storeHealth = new StoreHealth(new StoreHealthOptions());

        // snippet: sliding-window-fallback
        var window = TimeSpan.FromMinutes(1);

        var fallback = new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = window,
            SegmentsPerWindow = 6,
            QueueLimit = 0,
        });

        // The recovery time is the whole window, not one segment: a caller only fully refills once the whole window has rolled over.
        var fallbackRecoveryTime = window;
        // end-snippet

        var options = new ResilientRateLimiterOptions { FallbackRecoveryTime = fallbackRecoveryTime };
        using var limiter = primary.WithResilience(fallback, options, storeHealth);
        using var lease = await limiter.AcquireAsync(1);
        Console.WriteLine($"Allowed: {lease.IsAcquired}, fallback recovery time: {fallbackRecoveryTime}");
    }

    public static async Task BuiltInLimiter()
    {
        // snippet: built-in-limiter
        using var limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 2,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });

        for (var request = 1; request <= 3; request++)
        {
            using var lease = await limiter.AcquireAsync(permitCount: 1);

            if (lease.IsAcquired)
            {
                Console.WriteLine($"Request {request}: allowed");
            }
            else
            {
                lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter);
                Console.WriteLine($"Request {request}: rejected, retry after {retryAfter}");
            }
        }
        // end-snippet
    }

    public static Task LimiterKinds()
    {
        // snippet: limiter-kinds
        // At most 100 requests in each fixed minute (10:00:00-10:00:59, then 10:01:00-10:01:59, ...).
        using var fixedWindow = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 100,
            Window = TimeSpan.FromMinutes(1),
        });

        // At most 100 requests in any rolling minute, tracked in 6 segments of 10 seconds.
        using var slidingWindow = new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 100,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
        });

        // A bucket of 20 tokens; 10 tokens are added back every second.
        using var tokenBucket = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 20,
            TokensPerPeriod = 10,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        });

        // At most 5 requests running at the same time; a permit comes back when its lease is disposed.
        using var concurrency = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = 5,
        });
        // end-snippet

        Console.WriteLine("Built one limiter of each kind.");

        return Task.CompletedTask;
    }

    public static async Task BuiltInPartitioned()
    {
        // snippet: built-in-partitioned
        using var limiter = PartitionedRateLimiter.Create<string, string>(clientId =>
            RateLimitPartition.GetFixedWindowLimiter(clientId, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 1,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

        using var first = await limiter.AcquireAsync("client-a");
        using var second = await limiter.AcquireAsync("client-a");
        using var other = await limiter.AcquireAsync("client-b");

        Console.WriteLine($"client-a first: {first.IsAcquired}, client-a second: {second.IsAcquired}, client-b: {other.IsAcquired}");
        // end-snippet
    }

    public static async Task PartitionNoFallback()
    {
        var storeHealth = new StoreHealth(new StoreHealthOptions());

        // snippet: partition-no-fallback
        var options = new ResilientRateLimiterOptions { FailureBehavior = StoreFailureBehavior.FailClosed };

        using var limiter = PartitionedRateLimiter.Create<string, string>(key =>
            ResilientRateLimitPartition.Get(
                key,
                partitionKey => new UnreachableStore(),
                fallbackFactory: null,
                options,
                storeHealth));
        // end-snippet

        using var lease = await limiter.AcquireAsync("tenant-a", 1);
        Console.WriteLine($"Allowed: {lease.IsAcquired}");
    }

    public static async Task StoreHealthTimeProvider()
    {
        var primary = new InMemoryStore(permitLimit: 5, window: TimeSpan.FromSeconds(10));
        var fallback = new InMemoryStore(permitLimit: 2, window: TimeSpan.FromSeconds(10));
        var options = new ResilientRateLimiterOptions { FallbackRecoveryTime = TimeSpan.FromMinutes(1) };

        // snippet: store-health-time-provider
        var timeProvider = TimeProvider.System; // tests pass a fake TimeProvider instead

        var storeHealth = new StoreHealth(new StoreHealthOptions(), timeProvider);
        using var limiter = primary.WithResilience(fallback, options, storeHealth, timeProvider);
        // end-snippet

        using var lease = await limiter.AcquireAsync(1);
        Console.WriteLine($"Allowed: {lease.IsAcquired}");
    }

    public static Task StoreHealthValidate()
    {
        // snippet: store-health-validate
        var invalid = new StoreHealthOptions
        {
            FailuresBeforeOpen = 1,
            BreakerSamplingDuration = TimeSpan.FromMilliseconds(100),
        };

        try
        {
            _ = new StoreHealth(invalid);
        }
        catch (InvalidOperationException exception)
        {
            Console.WriteLine(exception.Message);
        }
        // end-snippet

        return Task.CompletedTask;
    }

    public static async Task LimiterOptionsAll()
    {
        var primary = new InMemoryStore(permitLimit: 5, window: TimeSpan.FromSeconds(10));
        var fallback = new InMemoryStore(permitLimit: 2, window: TimeSpan.FromSeconds(10));
        var storeHealth = new StoreHealth(new StoreHealthOptions());

        // snippet: limiter-options-all
        var options = new ResilientRateLimiterOptions
        {
            FailureBehavior = StoreFailureBehavior.LocalFallback,
            FallbackRecoveryTime = TimeSpan.FromSeconds(10),
            MaxWarmRetention = TimeSpan.FromMinutes(2),
            PolicyName = "orders-api",
            TagMetricsByPartitionKey = false,
            MaxAddedRetryDelay = TimeSpan.FromSeconds(60),
        };
        // end-snippet

        using var limiter = primary.WithResilience(fallback, options, storeHealth);
        using var lease = await limiter.AcquireAsync(1);
        Console.WriteLine($"Allowed: {lease.IsAcquired}");
    }

    public static async Task DisposeAsyncScenario()
    {
        var storeHealth = new StoreHealth(new StoreHealthOptions());
        var options = new ResilientRateLimiterOptions { FallbackRecoveryTime = TimeSpan.FromMinutes(1) };
        var primary = new InMemoryStore(permitLimit: 5, window: TimeSpan.FromSeconds(10));
        var fallback = new InMemoryStore(permitLimit: 2, window: TimeSpan.FromSeconds(10));

        // snippet: dispose-async
        await using var limiter = primary.WithResilience(fallback, options, storeHealth);
        // end-snippet

        using var lease = await limiter.AcquireAsync(1);
        Console.WriteLine($"Allowed: {lease.IsAcquired}");
    }
}
