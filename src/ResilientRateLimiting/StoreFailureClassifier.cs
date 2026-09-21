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

        if (options.ShouldHandle is { } shouldHandle)
        {
            return shouldHandle(exception);
        }

        return exception switch
        {
            TimeoutRejectedException => true,
            BrokenCircuitException => true,

            // Above the programming errors: TaskCanceledException is an OperationCanceledException.
            OperationCanceledException => false,

            ArgumentException => false,
            ObjectDisposedException => false,
            InvalidOperationException => false,

            _ => true,
        };
    }
}
