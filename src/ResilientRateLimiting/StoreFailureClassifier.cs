using Polly.CircuitBreaker;
using Polly.Timeout;

namespace ResilientRateLimiting;

/// <summary>Decides whether an exception means the store failed, or should reach the caller.</summary>
internal static class StoreFailureClassifier
{
    public static bool IsStoreFailure(Exception? exception, ResilientRateLimiterOptions options)
    {
        if (exception is null)
        {
            return false;
        }

        if (exception is OperationCanceledException)
        {
            return false;
        }

        if (options.ShouldHandle is { } shouldHandle)
        {
            return shouldHandle(exception);
        }

        return exception switch
        {
            TimeoutRejectedException => true,
            BrokenCircuitException => true,

            // Below the store-failure cases: the remaining programming errors reach the caller.
            ArgumentException => false,
            ObjectDisposedException => false,
            InvalidOperationException => false,

            _ => true,
        };
    }
}
