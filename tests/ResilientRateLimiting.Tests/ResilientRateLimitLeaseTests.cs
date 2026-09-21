using System.Threading.RateLimiting;
using Xunit;

namespace ResilientRateLimiting.Tests;

public class ResilientRateLimitLeaseTests
{
    private const string ForwardedValue = "forwarded";

    private sealed class StubLease(bool acquired, TimeSpan? retryAfter = null, string[]? ownNames = null)
        : RateLimitLease
    {
        public override bool IsAcquired => acquired;

        public override IEnumerable<string> MetadataNames
        {
            get
            {
                foreach (var name in ownNames ?? [])
                {
                    yield return name;
                }

                if (retryAfter is not null)
                {
                    yield return MetadataName.RetryAfter.Name;
                }
            }
        }

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (metadataName == MetadataName.RetryAfter.Name && retryAfter is { } value)
            {
                metadata = value;
                return true;
            }

            if (ownNames is not null && ownNames.Contains(metadataName))
            {
                metadata = ForwardedValue;
                return true;
            }

            metadata = null;
            return false;
        }
    }

    [Fact]
    public void Reports_every_name_the_inner_lease_reports_then_its_own()
    {
        using var lease = new ResilientRateLimitLease(
            new StubLease(acquired: false, ownNames: ["CUSTOM_ONE", "CUSTOM_TWO"]),
            LeaseSource.Distributed,
            retryAfter: TimeSpan.FromSeconds(9));

        var names = lease.MetadataNames.ToArray();

        Assert.Equal(
            [
                "CUSTOM_ONE",
                "CUSTOM_TWO",
                ResilientRateLimitLease.SourceMetadata.Name,
                MetadataName.RetryAfter.Name,
            ],
            names);

        Assert.True(lease.TryGetMetadata("CUSTOM_ONE", out var forwarded));
        Assert.Equal(ForwardedValue, forwarded);
    }

    [Fact]
    public void Does_not_repeat_a_name_the_inner_lease_already_reports()
    {
        using var lease = new ResilientRateLimitLease(
            new StubLease(acquired: true, ownNames: [ResilientRateLimitLease.SourceMetadata.Name]),
            LeaseSource.LocalFallback);

        var names = lease.MetadataNames.ToArray();

        Assert.Equal(1, names.Count(name => name == ResilientRateLimitLease.SourceMetadata.Name));
    }

    [Fact]
    public void Reports_the_source_it_was_created_with()
    {
        using var lease = new ResilientRateLimitLease(new StubLease(acquired: true), LeaseSource.LocalFallback);

        Assert.True(lease.IsAcquired);
        Assert.Equal(LeaseSource.LocalFallback, lease.Source);
        Assert.True(lease.TryGetMetadata(ResilientRateLimitLease.SourceMetadata, out var source));
        Assert.Equal(LeaseSource.LocalFallback, source);
        Assert.Contains(ResilientRateLimitLease.SourceMetadata.Name, lease.MetadataNames);
    }

    [Fact]
    public void Publishes_a_namespaced_metadata_key()
    {
        Assert.Equal("ResilientRateLimiting.Source", ResilientRateLimitLease.SourceMetadata.Name);
    }

    [Fact]
    public void Prefers_the_inner_retry_after_over_the_supplied_one()
    {
        using var lease = new ResilientRateLimitLease(
            new StubLease(acquired: false, retryAfter: TimeSpan.FromSeconds(7)),
            LeaseSource.Distributed,
            retryAfter: TimeSpan.FromSeconds(30));

        Assert.True(lease.TryGetMetadata(MetadataName.RetryAfter.Name, out var value));
        Assert.Equal(TimeSpan.FromSeconds(7), value);
    }

    [Fact]
    public void Serves_the_supplied_retry_after_when_the_inner_lease_has_none()
    {
        using var lease = new ResilientRateLimitLease(
            new StubLease(acquired: false),
            LeaseSource.LocalFallback,
            retryAfter: TimeSpan.FromSeconds(30));

        Assert.True(lease.TryGetMetadata(MetadataName.RetryAfter.Name, out var value));
        Assert.Equal(TimeSpan.FromSeconds(30), value);
        Assert.Contains(MetadataName.RetryAfter.Name, lease.MetadataNames);
    }

    [Fact]
    public void Reports_no_retry_after_when_neither_side_has_one()
    {
        using var lease = new ResilientRateLimitLease(new StubLease(acquired: true), LeaseSource.Distributed);

        Assert.False(lease.TryGetMetadata(MetadataName.RetryAfter.Name, out _));
        Assert.DoesNotContain(MetadataName.RetryAfter.Name, lease.MetadataNames);
    }
}
