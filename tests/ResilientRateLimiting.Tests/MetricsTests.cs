using Microsoft.Extensions.Time.Testing;
using System.Diagnostics.Metrics;
using Xunit;

namespace ResilientRateLimiting.Tests;

// The Meter behind ResilientRateLimiterMetrics is a process-wide singleton and xUnit runs test
// classes in parallel, so every test here uses its own Guid PolicyName and filters the recorded
// measurements by it. Never assert over the unfiltered stream.
public class MetricsTests
{
    private static ResilientRateLimiterOptions Options(
        string policyName,
        StoreFailureBehavior behavior = StoreFailureBehavior.LocalFallback,
        bool tagByPartitionKey = false,
        string? partitionKey = null) => new()
    {
        FallbackRecoveryTime = TimeSpan.FromMinutes(1),
        PolicyName = policyName,
        FailureBehavior = behavior,
        TagMetricsByPartitionKey = tagByPartitionKey,
        PartitionKey = partitionKey,
    };

    private static bool HasPolicy(Dictionary<string, object?> tags, string policy) =>
        tags.TryGetValue("policy", out var value) && Equals(value, policy);

    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener = new();

        private readonly List<(string Instrument, long Value, Dictionary<string, object?> Tags)> _measurements = [];

        // Every Recorder listens on the same process-wide Meter, so a test running in parallel can
        // append here while this one enumerates. Snapshot under the lock rather than exposing the
        // list itself.
        public IReadOnlyList<(string Instrument, long Value, Dictionary<string, object?> Tags)> Measurements
        {
            get
            {
                lock (_measurements)
                {
                    return [.. _measurements];
                }
            }
        }

        public Recorder(Action<Instrument, long, ReadOnlySpan<KeyValuePair<string, object?>>>? onMeasurement = null)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ResilientRateLimiterMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };

            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                onMeasurement?.Invoke(instrument, value, tags);

                var copy = new Dictionary<string, object?>();

                foreach (var tag in tags)
                {
                    copy[tag.Key] = tag.Value;
                }

                lock (_measurements)
                {
                    _measurements.Add((instrument.Name, value, copy));
                }
            });

            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public async Task Records_an_allowed_distributed_lease()
    {
        var policy = Guid.NewGuid().ToString();
        using var recorder = new Recorder();
        using var primary = new FakeRateLimiter(permitLimit: 10);
        using var fallback = new FakeRateLimiter(permitLimit: 10);
        var storeHealth = new StoreHealth(new StoreHealthOptions(), new FakeTimeProvider());
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(policy), storeHealth, new FakeTimeProvider());

        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();

        var lease = Assert.Single(
            recorder.Measurements,
            m => m.Instrument == "resilient_rate_limiting.leases" && HasPolicy(m.Tags, policy));
        Assert.Equal(policy, lease.Tags["policy"]);
        Assert.Equal("allowed", lease.Tags["outcome"]);
        Assert.Equal("distributed", lease.Tags["source"]);
        Assert.False(lease.Tags.ContainsKey("partition_key"));
    }

    [Fact]
    public async Task Records_a_limited_outcome_when_the_store_refuses()
    {
        var policy = Guid.NewGuid().ToString();
        using var recorder = new Recorder();
        using var primary = new FakeRateLimiter(permitLimit: 0);
        using var fallback = new FakeRateLimiter(permitLimit: 10);
        var storeHealth = new StoreHealth(new StoreHealthOptions(), new FakeTimeProvider());
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(policy), storeHealth, new FakeTimeProvider());

        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();

        var lease = Assert.Single(
            recorder.Measurements,
            m => m.Instrument == "resilient_rate_limiting.leases" && HasPolicy(m.Tags, policy));
        Assert.Equal("limited", lease.Tags["outcome"]);
        Assert.Equal("distributed", lease.Tags["source"]);
    }

    [Fact]
    public async Task Records_a_store_failure_with_its_exception_type()
    {
        var policy = Guid.NewGuid().ToString();
        using var recorder = new Recorder();
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FakeRateLimiter(permitLimit: 10);
        var storeHealth = new StoreHealth(new StoreHealthOptions(), new FakeTimeProvider());
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(policy), storeHealth, new FakeTimeProvider());

        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();

        var failure = Assert.Single(
            recorder.Measurements,
            m => m.Instrument == "resilient_rate_limiting.store_failures" && HasPolicy(m.Tags, policy));
        Assert.Equal(nameof(InvalidDataException), failure.Tags["exception_type"]);

        var lease = Assert.Single(
            recorder.Measurements,
            m => m.Instrument == "resilient_rate_limiting.leases" && HasPolicy(m.Tags, policy));
        Assert.Equal("local_fallback", lease.Tags["source"]);
    }

    [Fact]
    public async Task Tags_the_fail_open_source()
    {
        var policy = Guid.NewGuid().ToString();
        using var recorder = new Recorder();
        var options = Options(policy, behavior: StoreFailureBehavior.FailOpen);
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        var storeHealth = new StoreHealth(new StoreHealthOptions(), new FakeTimeProvider());
        using var limiter = new ResilientRateLimiter(primary, fallback: null, options, storeHealth, new FakeTimeProvider());

        var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        lease.Dispose();

        Assert.True(lease.IsAcquired);

        var measurement = Assert.Single(
            recorder.Measurements,
            m => m.Instrument == "resilient_rate_limiting.leases" && HasPolicy(m.Tags, policy));
        Assert.Equal("fail_open", measurement.Tags["source"]);
        Assert.Equal("allowed", measurement.Tags["outcome"]);
    }

    [Fact]
    public async Task Tags_the_fail_closed_source()
    {
        var policy = Guid.NewGuid().ToString();
        using var recorder = new Recorder();
        var options = Options(policy, behavior: StoreFailureBehavior.FailClosed);
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        var storeHealth = new StoreHealth(new StoreHealthOptions(), new FakeTimeProvider());
        using var limiter = new ResilientRateLimiter(primary, fallback: null, options, storeHealth, new FakeTimeProvider());

        var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        lease.Dispose();

        Assert.False(lease.IsAcquired);

        var measurement = Assert.Single(
            recorder.Measurements,
            m => m.Instrument == "resilient_rate_limiting.leases" && HasPolicy(m.Tags, policy));
        Assert.Equal("fail_closed", measurement.Tags["source"]);
        Assert.Equal("limited", measurement.Tags["outcome"]);
    }

    [Fact]
    public async Task Tags_the_recovery_source_for_a_gate_refusal()
    {
        var policy = Guid.NewGuid().ToString();
        var clock = new FakeTimeProvider();
        using var recorder = new Recorder();
        var options = Options(policy);
        var storeOptions = new StoreHealthOptions
        {
            FailuresBeforeOpen = 2,
            BreakDuration = TimeSpan.FromSeconds(5),
            BreakerSamplingDuration = TimeSpan.FromSeconds(10),
        };
        using var primary = new FakeRateLimiter(permitLimit: 1000).FailTimes(2, new InvalidDataException("blip"));
        using var fallback = new FakeRateLimiter(permitLimit: 1);
        var storeHealth = new StoreHealth(storeOptions, clock);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, storeHealth, clock);
        var cancellationToken = TestContext.Current.CancellationToken;

        // Outage, then the store recovers and arms the recovery window; the local counter it
        // charged is now exhausted, so the next call is refused by the gate, not by the store.
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();
        clock.Advance(TimeSpan.FromSeconds(6));
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        var measurement = Assert.Single(
            recorder.Measurements,
            m => m.Instrument == "resilient_rate_limiting.leases" && HasPolicy(m.Tags, policy) && Equals(m.Tags["source"], "recovery"));
        Assert.Equal("limited", measurement.Tags["outcome"]);
    }

    [Fact]
    public async Task Tags_the_partition_key_only_when_asked()
    {
        var policy = Guid.NewGuid().ToString();
        using var recorder = new Recorder();
        var options = Options(policy, tagByPartitionKey: true, partitionKey: "acme");

        using var primary = new FakeRateLimiter(permitLimit: 10);
        using var fallback = new FakeRateLimiter(permitLimit: 10);
        var storeHealth = new StoreHealth(new StoreHealthOptions(), new FakeTimeProvider());
        using var limiter = new ResilientRateLimiter(primary, fallback, options, storeHealth, new FakeTimeProvider());

        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();

        var lease = Assert.Single(
            recorder.Measurements,
            m => m.Instrument == "resilient_rate_limiting.leases" && HasPolicy(m.Tags, policy));
        Assert.Equal("acme", lease.Tags["partition_key"]);
    }

    [Fact]
    public async Task Raises_the_failure_callback_once_per_distinct_exception_type()
    {
        var policy = Guid.NewGuid().ToString();
        var reported = new List<Exception>();
        var storeHealth = new StoreHealth(new StoreHealthOptions { OnStoreFailure = reported.Add }, new FakeTimeProvider());

        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FakeRateLimiter(permitLimit: 10);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(policy), storeHealth, new FakeTimeProvider());

        for (var i = 0; i < 3; i++)
        {
            (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        }

        Assert.Single(reported);

        primary.AlwaysFail(new IOException("socket closed"));
        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(2, reported.Count);
    }

    [Fact]
    public async Task A_new_store_health_instance_reports_the_same_exception_type_again()
    {
        var policy = Guid.NewGuid().ToString();
        var cancellationToken = TestContext.Current.CancellationToken;

        var reportedFirst = new List<Exception>();
        var storeHealthA = new StoreHealth(new StoreHealthOptions { OnStoreFailure = reportedFirst.Add }, new FakeTimeProvider());
        using var primaryA = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallbackA = new FakeRateLimiter(permitLimit: 10);
        using var limiterA = new ResilientRateLimiter(primaryA, fallbackA, Options(policy), storeHealthA, new FakeTimeProvider());

        (await limiterA.AcquireAsync(1, cancellationToken)).Dispose();
        (await limiterA.AcquireAsync(1, cancellationToken)).Dispose();
        Assert.Single(reportedFirst);

        // A second StoreHealth instance, same exception type: its own memory has seen nothing yet.
        var reportedSecond = new List<Exception>();
        var storeHealthB = new StoreHealth(new StoreHealthOptions { OnStoreFailure = reportedSecond.Add }, new FakeTimeProvider());
        using var primaryB = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallbackB = new FakeRateLimiter(permitLimit: 10);
        using var limiterB = new ResilientRateLimiter(primaryB, fallbackB, Options(policy), storeHealthB, new FakeTimeProvider());

        (await limiterB.AcquireAsync(1, cancellationToken)).Dispose();

        Assert.Single(reportedSecond);
    }

    [Fact]
    public async Task A_throwing_meter_listener_does_not_fail_the_request()
    {
        var policy = Guid.NewGuid().ToString();
        using var recorder = new Recorder((_, _, _) => throw new InvalidOperationException("listener exploded"));
        using var primary = new FakeRateLimiter(permitLimit: 10);
        using var fallback = new FakeRateLimiter(permitLimit: 10);
        var storeHealth = new StoreHealth(new StoreHealthOptions(), new FakeTimeProvider());
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(policy), storeHealth, new FakeTimeProvider());

        using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        Assert.True(lease.IsAcquired);
        Assert.True(lease.TryGetMetadata(ResilientRateLimitLease.SourceMetadata, out var source));
        Assert.Equal(LeaseSource.Distributed, source);
    }

    [Fact]
    public async Task A_throwing_store_failure_callback_does_not_fail_the_request()
    {
        var policy = Guid.NewGuid().ToString();
        var storeHealth = new StoreHealth(
            new StoreHealthOptions { OnStoreFailure = _ => throw new InvalidOperationException("callback exploded") },
            new FakeTimeProvider());

        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FakeRateLimiter(permitLimit: 10);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(policy), storeHealth, new FakeTimeProvider());

        using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        Assert.True(lease.IsAcquired);
        Assert.True(lease.TryGetMetadata(ResilientRateLimitLease.SourceMetadata, out var source));
        Assert.Equal(LeaseSource.LocalFallback, source);
    }
}
