using System.Threading.RateLimiting;
using Xunit;

namespace ResilientRateLimiting.Tests;

public class ResilientRateLimitLeaseTests
{
    private sealed class StubLease(bool acquired, TimeSpan? retryAfter = null) : RateLimitLease
    {
        public override bool IsAcquired => acquired;

        public override IEnumerable<string> MetadataNames =>
            retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (metadataName == MetadataName.RetryAfter.Name && retryAfter is { } value)
            {
                metadata = value;
                return true;
            }

            metadata = null;
            return false;
        }
    }

    [Fact]
    public void Reports_the_source_it_was_created_with()
    {
        using var lease = new ResilientRateLimitLease(new StubLease(acquired: true), LeaseSource.LocalFallback);

        Assert.True(lease.IsAcquired);
        Assert.Equal(LeaseSource.LocalFallback, lease.Source);
        Assert.True(lease.TryGetMetadata(ResilientRateLimitLease.SourceMetadataName, out var source));
        Assert.Equal(LeaseSource.LocalFallback, source);
        Assert.Contains(ResilientRateLimitLease.SourceMetadataName, lease.MetadataNames);
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
