using System.Threading.RateLimiting;

namespace ResilientRateLimiting;

/// <summary>A lease carrying no metadata, for the fail-open, fail-closed and synchronous paths.</summary>
internal sealed class StaticLease(bool acquired) : RateLimitLease
{
    public static readonly StaticLease Acquired = new(true);

    public static readonly StaticLease Rejected = new(false);

    public override bool IsAcquired => acquired;

    public override IEnumerable<string> MetadataNames => [];

    public override bool TryGetMetadata(string metadataName, out object? metadata)
    {
        metadata = null;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        // Shared instances outlive any single lease.
    }
}
