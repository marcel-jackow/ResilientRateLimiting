using System.Threading.RateLimiting;

namespace ResilientRateLimiting;

/// <summary>Wraps a lease from an inner limiter and records which path produced it.</summary>
public sealed class ResilientRateLimitLease : RateLimitLease
{
    /// <summary>Metadata key carrying the <see cref="LeaseSource"/> of this lease.</summary>
    public static readonly MetadataName<LeaseSource> SourceMetadata =
        MetadataName.Create<LeaseSource>("ResilientRateLimiting.Source");

    private readonly RateLimitLease _inner;
    private readonly TimeSpan? _retryAfter;

    /// <param name="inner">The lease produced by the limiter that answered.</param>
    /// <param name="source">Which path answered.</param>
    /// <param name="retryAfter">When given, replaces <paramref name="inner"/>'s own <see cref="MetadataName.RetryAfter"/> value.</param>
    public ResilientRateLimitLease(RateLimitLease inner, LeaseSource source, TimeSpan? retryAfter = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _retryAfter = retryAfter;
        Source = source;
    }

    /// <summary>Which path produced this lease.</summary>
    public LeaseSource Source { get; }

    /// <inheritdoc />
    public override bool IsAcquired => _inner.IsAcquired;

    /// <inheritdoc />
    public override IEnumerable<string> MetadataNames
    {
        get
        {
            var innerNames = _inner.MetadataNames.ToArray();

            foreach (var name in innerNames)
            {
                yield return name;
            }

            if (!innerNames.Contains(SourceMetadata.Name))
            {
                yield return SourceMetadata.Name;
            }

            if (_retryAfter is not null && !innerNames.Contains(MetadataName.RetryAfter.Name))
            {
                yield return MetadataName.RetryAfter.Name;
            }
        }
    }

    /// <inheritdoc />
    public override bool TryGetMetadata(string metadataName, out object? metadata)
    {
        if (metadataName == SourceMetadata.Name)
        {
            metadata = Source;
            return true;
        }

        if (metadataName == MetadataName.RetryAfter.Name && _retryAfter is { } value)
        {
            metadata = value;
            return true;
        }

        return _inner.TryGetMetadata(metadataName, out metadata);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
    }
}
