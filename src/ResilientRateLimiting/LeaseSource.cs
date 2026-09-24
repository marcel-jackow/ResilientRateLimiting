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

    /// <summary>After an outage, the store did not count the requests replicas served locally. So for <see cref="ResilientRateLimiterOptions.FallbackRecoveryTime"/>, the local counter is asked first, and a refusal here stops a client from getting a second allowance: once from the local fallback during the outage, and once from the store right after it.</summary>
    Recovery,
}
