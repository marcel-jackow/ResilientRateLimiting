using System.Threading.RateLimiting;

namespace ResilientRateLimiting;

/// <summary>Wraps a lease from an inner limiter and records which path produced it.</summary>
public sealed class ResilientRateLimitLease : RateLimitLease
{
    /// <summary>Metadata key carrying the <see cref="LeaseSource"/> of this lease.</summary>
    /// <example>
    /// <code>
    /// if (lease.TryGetMetadata(ResilientRateLimitLease.SourceMetadata, out var source))
    /// {
    ///     // source is a ResilientRateLimiting.LeaseSource, such as LeaseSource.LocalFallback
    /// }
    /// </code>
    /// </example>
    public static readonly MetadataName<LeaseSource> SourceMetadata =
        MetadataName.Create<LeaseSource>("ResilientRateLimiting.Source");

    private readonly RateLimitLease _inner;
    private readonly TimeSpan? _retryAfter;

    /// <param name="inner">The lease produced by the limiter that answered.</param>
    /// <param name="source">Which path answered.</param>
    /// <param name="retryAfter">When given, replaces <paramref name="inner"/>'s own <see cref="MetadataName.RetryAfter"/> value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is <see langword="null"/>.</exception>
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

    /// <summary>The inner lease's metadata names, plus <see cref="SourceMetadata"/>, plus <see cref="MetadataName.RetryAfter"/> when this lease carries its own Retry-After value that the inner lease does not already report.</summary>
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

    /// <summary>Reads one piece of metadata by name. Answers <see cref="SourceMetadata"/> and, when this lease carries its own Retry-After value, <see cref="MetadataName.RetryAfter"/> itself; every other name is passed through to the inner lease.</summary>
    /// <param name="metadataName">The metadata name to look up, typically <see cref="SourceMetadata"/>'s <see cref="MetadataName{T}.Name"/> or one from <see cref="MetadataNames"/>.</param>
    /// <param name="metadata">The value found, or <see langword="null"/> when <paramref name="metadataName"/> is not present.</param>
    /// <returns><see langword="true"/> if <paramref name="metadataName"/> was found.</returns>
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

    /// <summary>Disposes the inner lease.</summary>
    /// <param name="disposing"><see langword="true"/> when called from <see cref="IDisposable.Dispose"/> rather than a finalizer.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
    }
}
