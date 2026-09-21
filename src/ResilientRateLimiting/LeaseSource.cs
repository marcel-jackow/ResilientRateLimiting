namespace ResilientRateLimiting;

/// <summary>Which path produced a lease.</summary>
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
}
