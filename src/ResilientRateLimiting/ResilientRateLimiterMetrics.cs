using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ResilientRateLimiting;

/// <summary>The library's own instruments. Low cardinality by default: a library must be safe under the worst partitioning its users will choose.</summary>
internal sealed class ResilientRateLimiterMetrics
{
    /// <summary>The meter name to subscribe to.</summary>
    public const string MeterName = ResilientRateLimiter.MeterName;

    public static readonly ResilientRateLimiterMetrics Shared = new();

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _leases;
    private readonly Counter<long> _storeFailures;

    private ResilientRateLimiterMetrics()
    {
        _leases = _meter.CreateCounter<long>(
            "resilient_rate_limiting.leases",
            unit: "{lease}",
            description: "Rate limiting decisions, tagged with the path that produced them.");

        _storeFailures = _meter.CreateCounter<long>(
            "resilient_rate_limiting.store_failures",
            unit: "{failure}",
            description: "Store calls that failed and took the fallback path, by exception type.");
    }

    /// <summary>Records one lease decision. Never throws: a misbehaving <see cref="System.Diagnostics.Metrics.MeterListener"/> must not fail the request it is observing.</summary>
    public void RecordLease(ResilientRateLimiterOptions options, LeaseSource source, bool acquired)
    {
        try
        {
            var tags = new TagList
            {
                { "policy", options.PolicyName },
                { "outcome", acquired ? "allowed" : "limited" },
                { "source", SourceTag(source) },
            };

            if (options.TagMetricsByPartitionKey && options.PartitionKey is { } key)
            {
                tags.Add("partition_key", key);
            }

            _leases.Add(1, tags);
        }
        catch
        {
            // See the summary: an observability failure must not become a request failure.
        }
    }

    /// <summary>Records one classified store failure. Never throws, for the same reason as <see cref="RecordLease"/>.</summary>
    public void RecordStoreFailure(ResilientRateLimiterOptions options, Exception exception)
    {
        try
        {
            _storeFailures.Add(
                1,
                new TagList { { "policy", options.PolicyName }, { "exception_type", exception.GetType().Name } });
        }
        catch
        {
            // See RecordLease.
        }
    }

    private static string SourceTag(LeaseSource source) => source switch
    {
        LeaseSource.Distributed => "distributed",
        LeaseSource.LocalFallback => "local_fallback",
        LeaseSource.FailOpen => "fail_open",
        LeaseSource.FailClosed => "fail_closed",
        LeaseSource.Recovery => "recovery",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown lease source."),
    };
}
