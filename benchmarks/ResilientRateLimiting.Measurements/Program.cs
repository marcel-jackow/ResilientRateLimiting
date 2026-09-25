using RedisRateLimiting;
using ResilientRateLimiting;
using StackExchange.Redis;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading.RateLimiting;
using Testcontainers.Redis;

// Usage: dotnet run -c Release --project benchmarks/ResilientRateLimiting.Measurements -- <footprint-redis|footprint-memory|roundtrip>
// Run each footprint mode three times in separate processes and report the median.

const int Partitions = 10_000;
const int AlarmBytesPerPartition = 2_048;
const int Warmup = 500;
const int Samples = 5_000;
const string RedisImage = "redis:7-alpine";

var mode = args.Length == 1 ? args[0] : "";

if (mode is not ("footprint-redis" or "footprint-memory" or "roundtrip"))
{
    Console.Error.WriteLine("Mode: footprint-redis | footprint-memory | roundtrip");
    return 2;
}

Console.WriteLine($"| Date | {DateTime.UtcNow:yyyy-MM-dd} |");
Console.WriteLine($"| OS | {RuntimeInformation.OSDescription} |");
Console.WriteLine($"| Runtime | {RuntimeInformation.FrameworkDescription} |");
Console.WriteLine($"| Logical processors | {Environment.ProcessorCount} |");
Console.WriteLine($"| GC | {(GCSettings.IsServerGC ? "server" : "workstation")} |");
Console.WriteLine($"| Redis | {RedisImage} in a local Docker container |");
Console.WriteLine();

var limiterOptions = new ResilientRateLimiterOptions { FallbackRecoveryTime = TimeSpan.FromMinutes(1) };

await using var container = new RedisBuilder(RedisImage).Build();
await container.StartAsync();
await using var connection = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());

static FixedWindowRateLimiter Fixed(int permits) => new(new FixedWindowRateLimiterOptions
{
    PermitLimit = permits,
    Window = TimeSpan.FromMinutes(1),
    QueueLimit = 0,
});

RedisSlidingWindowRateLimiter<string> Redis(string key, int permits) => new(key, new RedisSlidingWindowRateLimiterOptions
{
    PermitLimit = permits,
    Window = TimeSpan.FromMinutes(1),
    ConnectionMultiplexerFactory = () => connection,
});

// A generous timeout so a slow first call on a laptop does not turn the measurement into a fallback measurement.
var relaxed = new StoreHealth(new StoreHealthOptions
{
    StoreTimeout = TimeSpan.FromSeconds(2),
    MaxWarmPartitions = Partitions * 2,
});

RateLimiter Primary(int i) => mode == "footprint-memory" ? Fixed(100) : Redis($"partition-{i}", 100);

ResilientRateLimiter Build(int i) =>
    new(Primary(i), Fixed(LocalBudget.ForReplicas(100, 3)), limiterOptions, relaxed);

if (mode.StartsWith("footprint", StringComparison.Ordinal))
{
    // Moves statics, JIT, the Lua script load and first-use caches out of the measured window.
    using (var warm = Build(-1))
    {
        (await warm.AcquireAsync(1)).Dispose();
    }

    var limiters = new List<ResilientRateLimiter>(Partitions);
    var before = Settle();

    for (var i = 0; i < Partitions; i++)
    {
        limiters.Add(Build(i));
    }

    var afterBuild = Settle();

    foreach (var limiter in limiters)
    {
        (await limiter.AcquireAsync(1)).Dispose();
    }

    var afterUse = Settle();
    var perPartition = (afterUse - before) / (double)Partitions;

    Console.WriteLine("| Primary | Fallback | Partitions | Bytes per partition after creation | Bytes per partition after one request each |");
    Console.WriteLine("|---|---|---|---|---|");
    Console.WriteLine($"| {(mode == "footprint-memory" ? "FixedWindowRateLimiter (in memory)" : "RedisSlidingWindowRateLimiter")} | FixedWindowRateLimiter | {Partitions} | {(afterBuild - before) / (double)Partitions:F0} | {perPartition:F0} |");

    foreach (var limiter in limiters)
    {
        limiter.Dispose();
    }

    GC.KeepAlive(limiters);

    if (perPartition > AlarmBytesPerPartition)
    {
        Console.Error.WriteLine($"ALARM: {perPartition:F0} bytes per partition is above {AlarmBytesPerPartition}. Something is kept alive that should not be.");
        return 1;
    }

    return 0;
}

var db = connection.GetDatabase();
Console.WriteLine("| Call | Samples | p50 ms | p90 ms | p99 ms | p99.9 ms | max ms |");
Console.WriteLine("|---|---|---|---|---|---|---|");
Report("Redis PING (network floor)", await Sample(() => db.PingAsync()));

var raw = Redis("roundtrip-raw", 1_000_000);
Report("RedisSlidingWindowRateLimiter alone", await Sample(async () => (await raw.AcquireAsync(1)).Dispose()));

var defaultHealth = new StoreHealth(new StoreHealthOptions());
using var wrapped = new ResilientRateLimiter(Redis("roundtrip-wrapped", 1_000_000), Fixed(1_000_000), limiterOptions, defaultHealth);
var notDistributed = 0;
Report("ResilientRateLimiter, default StoreTimeout", await Sample(async () =>
{
    using var lease = await wrapped.AcquireAsync(1);

    if (lease.TryGetMetadata(ResilientRateLimitLease.SourceMetadata, out var source) && source != LeaseSource.Distributed)
    {
        notDistributed++;
    }
}));
Console.WriteLine();
Console.WriteLine($"Requests not answered by the store at the default StoreTimeout (warm-up included): {notDistributed} of {Warmup + Samples}");
return 0;

static long Settle()
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    return GC.GetTotalMemory(forceFullCollection: true);
}

static async Task<List<double>> Sample(Func<Task> call)
{
    for (var i = 0; i < Warmup; i++)
    {
        await call();
    }

    var samples = new List<double>(Samples);

    for (var i = 0; i < Samples; i++)
    {
        var start = Stopwatch.GetTimestamp();
        await call();
        samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    return samples;
}

static void Report(string label, List<double> ms)
{
    ms.Sort();
    double P(double p) => ms[(int)Math.Min(ms.Count - 1, Math.Floor(p * ms.Count))];
    Console.WriteLine($"| {label} | {ms.Count} | {P(0.50):F2} | {P(0.90):F2} | {P(0.99):F2} | {P(0.999):F2} | {ms[^1]:F2} |");
}
