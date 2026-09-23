using Microsoft.Extensions.Time.Testing;
using System.Diagnostics.Metrics;
using System.Threading.RateLimiting;
using Xunit;

namespace ResilientRateLimiting.Tests;

// The Meter behind ResilientRateLimiterMetrics is a process-wide singleton and xUnit runs test
// classes in parallel, so the metric tests here use their own Guid PolicyName and filter by it,
// same as MetricsTests.
public class PublicApiTests
{
    private static ResilientRateLimiterOptions Options(string? policyName = null) => new()
    {
        FallbackRecoveryTime = TimeSpan.FromMinutes(1),
        PolicyName = policyName ?? "default",
    };

    private static bool HasPolicy(Dictionary<string, object?> tags, string policy) =>
        tags.TryGetValue("policy", out var value) && Equals(value, policy);

    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string Instrument, Dictionary<string, object?> Tags)> _measurements = [];

        public IReadOnlyList<(string Instrument, Dictionary<string, object?> Tags)> Measurements
        {
            get
            {
                lock (_measurements)
                {
                    return [.. _measurements];
                }
            }
        }

        public Recorder()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ResilientRateLimiterMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };

            _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            {
                var copy = new Dictionary<string, object?>();

                foreach (var tag in tags)
                {
                    copy[tag.Key] = tag.Value;
                }

                lock (_measurements)
                {
                    _measurements.Add((instrument.Name, copy));
                }
            });

            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public async Task WithResilience_wraps_a_limiter()
    {
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FakeRateLimiter(permitLimit: 1);
        var storeHealth = new StoreHealth(new StoreHealthOptions(), new FakeTimeProvider());

        using var limiter = primary.WithResilience(fallback, Options(), storeHealth, new FakeTimeProvider());

        using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        Assert.True(lease.IsAcquired);
        Assert.True(lease.TryGetMetadata(ResilientRateLimitLease.SourceMetadata, out var source));
        Assert.Equal(LeaseSource.LocalFallback, source);
    }

    [Fact]
    public void WithResilience_throws_for_null_primary()
    {
        RateLimiter? primary = null;
        using var fallback = new FakeRateLimiter(permitLimit: 1);
        var storeHealth = new StoreHealth(new StoreHealthOptions());

        Assert.Throws<ArgumentNullException>(() => primary!.WithResilience(fallback, Options(), storeHealth));
    }

    [Fact]
    public void WithResilience_throws_for_null_options()
    {
        using var primary = new FakeRateLimiter();
        using var fallback = new FakeRateLimiter();
        var storeHealth = new StoreHealth(new StoreHealthOptions());

        Assert.Throws<ArgumentNullException>(() => primary.WithResilience(fallback, options: null!, storeHealth));
    }

    [Fact]
    public void WithResilience_throws_for_null_storeHealth()
    {
        using var primary = new FakeRateLimiter();
        using var fallback = new FakeRateLimiter();

        Assert.Throws<ArgumentNullException>(() => primary.WithResilience(fallback, Options(), storeHealth: null!));
    }

    [Fact]
    public async Task Each_partition_gets_its_own_fallback_limiter()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        var storeHealth = new StoreHealth(new StoreHealthOptions(), clock);
        var localPermitLimit = LocalBudget.ForReplicas(sharedPermitLimit: 6, replicaCount: 3);

        using var partitioned = PartitionedRateLimiter.Create<string, string>(tenant =>
            ResilientRateLimitPartition.Get(
                tenant,
                primaryFactory: _ => new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down")),
                fallbackFactory: _ => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
                {
                    PermitLimit = localPermitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = false,
                }),
                options,
                storeHealth,
                clock));

        var cancellationToken = TestContext.Current.CancellationToken;

        using var firstForA = await partitioned.AcquireAsync("tenant-a", localPermitLimit, cancellationToken);
        using var firstForB = await partitioned.AcquireAsync("tenant-b", localPermitLimit, cancellationToken);
        using var secondForA = await partitioned.AcquireAsync("tenant-a", 1, cancellationToken);

        Assert.True(firstForA.IsAcquired);
        Assert.True(firstForB.IsAcquired);   // tenant B has its own budget, unaffected by tenant A
        Assert.False(secondForA.IsAcquired); // tenant A already spent its own two permits
    }

    [Fact]
    public async Task All_partitions_share_the_one_store_health()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        var storeHealth = new StoreHealth(new StoreHealthOptions(), clock);

        using var partitioned = PartitionedRateLimiter.Create<string, string>(tenant =>
            ResilientRateLimitPartition.Get(
                tenant,
                primaryFactory: _ => new FakeRateLimiter(permitLimit: 10),
                fallbackFactory: _ => new FakeRateLimiter(permitLimit: 10),
                options,
                storeHealth,
                clock));

        var cancellationToken = TestContext.Current.CancellationToken;

        using var firstForA = await partitioned.AcquireAsync("tenant-a", 1, cancellationToken);
        using var firstForB = await partitioned.AcquireAsync("tenant-b", 1, cancellationToken);

        Assert.Equal(2, storeHealth.LivePartitions);
    }

    [Fact]
    public void Get_throws_for_null_primaryFactory()
    {
        var storeHealth = new StoreHealth(new StoreHealthOptions());

        Assert.Throws<ArgumentNullException>(() =>
            ResilientRateLimitPartition.Get<string>(
                "tenant-a",
                primaryFactory: null!,
                fallbackFactory: _ => new FakeRateLimiter(),
                Options(),
                storeHealth));
    }

    [Fact]
    public void Get_throws_for_null_options()
    {
        var storeHealth = new StoreHealth(new StoreHealthOptions());

        Assert.Throws<ArgumentNullException>(() =>
            ResilientRateLimitPartition.Get<string>(
                "tenant-a",
                primaryFactory: _ => new FakeRateLimiter(),
                fallbackFactory: _ => new FakeRateLimiter(),
                options: null!,
                storeHealth));
    }

    [Fact]
    public void Get_throws_for_null_storeHealth()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ResilientRateLimitPartition.Get<string>(
                "tenant-a",
                primaryFactory: _ => new FakeRateLimiter(),
                fallbackFactory: _ => new FakeRateLimiter(),
                Options(),
                storeHealth: null!));
    }

    [Fact]
    public async Task Tags_the_partition_key_per_key_when_opted_in()
    {
        var policy = Guid.NewGuid().ToString();
        using var recorder = new Recorder();
        var options = new ResilientRateLimiterOptions
        {
            FallbackRecoveryTime = TimeSpan.FromMinutes(1),
            PolicyName = policy,
            TagMetricsByPartitionKey = true,
        };
        var storeHealth = new StoreHealth(new StoreHealthOptions(), new FakeTimeProvider());

        using var partitioned = PartitionedRateLimiter.Create<string, string>(tenant =>
            ResilientRateLimitPartition.Get(
                tenant,
                primaryFactory: _ => new FakeRateLimiter(permitLimit: 10),
                fallbackFactory: _ => new FakeRateLimiter(permitLimit: 10),
                options,
                storeHealth,
                new FakeTimeProvider()));

        var cancellationToken = TestContext.Current.CancellationToken;
        using var leaseA = await partitioned.AcquireAsync("tenant-a", 1, cancellationToken);
        using var leaseB = await partitioned.AcquireAsync("tenant-b", 1, cancellationToken);

        Assert.Single(
            recorder.Measurements,
            m => m.Instrument == "resilient_rate_limiting.leases" && HasPolicy(m.Tags, policy) &&
                 Equals(m.Tags.GetValueOrDefault("partition_key"), "tenant-a"));
        Assert.Single(
            recorder.Measurements,
            m => m.Instrument == "resilient_rate_limiting.leases" && HasPolicy(m.Tags, policy) &&
                 Equals(m.Tags.GetValueOrDefault("partition_key"), "tenant-b"));
    }

    [Fact]
    public async Task Does_not_tag_the_partition_key_without_opting_in()
    {
        var policy = Guid.NewGuid().ToString();
        using var recorder = new Recorder();
        var options = Options(policy);
        var storeHealth = new StoreHealth(new StoreHealthOptions(), new FakeTimeProvider());

        using var partitioned = PartitionedRateLimiter.Create<string, string>(tenant =>
            ResilientRateLimitPartition.Get(
                tenant,
                primaryFactory: _ => new FakeRateLimiter(permitLimit: 10),
                fallbackFactory: _ => new FakeRateLimiter(permitLimit: 10),
                options,
                storeHealth,
                new FakeTimeProvider()));

        using var lease = await partitioned.AcquireAsync("tenant-a", 1, TestContext.Current.CancellationToken);

        var measurement = Assert.Single(
            recorder.Measurements,
            m => m.Instrument == "resilient_rate_limiting.leases" && HasPolicy(m.Tags, policy));
        Assert.False(measurement.Tags.ContainsKey("partition_key"));
    }

    [Fact]
    public async Task Keeps_the_configured_partition_key_when_already_set()
    {
        var policy = Guid.NewGuid().ToString();
        using var recorder = new Recorder();
        var options = new ResilientRateLimiterOptions
        {
            FallbackRecoveryTime = TimeSpan.FromMinutes(1),
            PolicyName = policy,
            TagMetricsByPartitionKey = true,
            PartitionKey = "configured",
        };
        var storeHealth = new StoreHealth(new StoreHealthOptions(), new FakeTimeProvider());

        using var partitioned = PartitionedRateLimiter.Create<string, string>(tenant =>
            ResilientRateLimitPartition.Get(
                tenant,
                primaryFactory: _ => new FakeRateLimiter(permitLimit: 10),
                fallbackFactory: _ => new FakeRateLimiter(permitLimit: 10),
                options,
                storeHealth,
                new FakeTimeProvider()));

        using var lease = await partitioned.AcquireAsync("tenant-a", 1, TestContext.Current.CancellationToken);

        var measurement = Assert.Single(
            recorder.Measurements,
            m => m.Instrument == "resilient_rate_limiting.leases" && HasPolicy(m.Tags, policy));
        Assert.Equal("configured", measurement.Tags["partition_key"]);
    }
}
