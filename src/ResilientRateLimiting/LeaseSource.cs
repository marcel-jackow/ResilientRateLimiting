namespace ResilientRateLimiting;

/// <summary>Which path produced a lease.</summary>
/// <remarks>Read this from a lease with <see cref="ResilientRateLimitLease.SourceMetadata"/>.</remarks>
public enum LeaseSource
{
    /// <summary>The shared store answered.</summary>
    Distributed,

    /// <summary>The store failed and the local fallback limiter answered.</summary>
    LocalFallback,

    /// <summary>The store failed and the configured behaviour was to admit the request.</summary>
    FailOpen,

    /// <summary>The store failed and the configured behaviour was to reject the request.</summary>
    FailClosed,

    /// <summary>The store recently answered again after an outage, and this request was refused by the local counter before the store was asked, so a burst of retries right after recovery does not overwhelm the store.</summary>
    Recovery,
}
