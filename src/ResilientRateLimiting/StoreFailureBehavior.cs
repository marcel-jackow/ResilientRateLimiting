namespace ResilientRateLimiting;

/// <summary>What to do when the shared store cannot answer.</summary>
/// <remarks>Set on <see cref="ResilientRateLimiterOptions.FailureBehavior"/>.</remarks>
public enum StoreFailureBehavior
{
    /// <summary>Ask the local fallback limiter. The default.</summary>
    LocalFallback,

    /// <summary>Admit the request. Rate limiting stops while the store is down.</summary>
    FailOpen,

    /// <summary>Reject the request.</summary>
    FailClosed,
}
