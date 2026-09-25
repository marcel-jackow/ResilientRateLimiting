using Polly.CircuitBreaker;
using Polly.Timeout;

namespace ResilientRateLimiting;

/// <summary>Decides whether an exception means the store failed, or should reach the caller.</summary>
internal static class StoreFailureClassifier
{
    public static bool IsStoreFailure(Exception? exception, Func<Exception, bool>? shouldHandle)
    {
        if (exception is null)
        {
            return false;
        }

        if (exception is OperationCanceledException)
        {
            return false;
        }

        // The library's own cutoffs always count as store failures, even under a caller predicate.
        if (exception is TimeoutRejectedException or BrokenCircuitException)
        {
            return true;
        }

        if (shouldHandle is not null)
        {
            return shouldHandle(exception);
        }

        return exception switch
        {
            // Below the store-failure cases: the remaining programming errors reach the caller.
            ArgumentException => false,
            ObjectDisposedException => false,
            InvalidOperationException => false,

            _ => true,
        };
    }
}
