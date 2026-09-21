using Polly.CircuitBreaker;
using Polly.Timeout;
using Xunit;

namespace ResilientRateLimiting.Tests;

public class StoreFailureClassifierTests
{
    public static TheoryData<Exception> StoreFailures =>
    [
        new TimeoutRejectedException(),
        new BrokenCircuitException(),
        new InvalidDataException("connection reset"),
        new IOException("socket closed"),
    ];

    public static TheoryData<Exception> Propagated =>
    [
        new OperationCanceledException(),
        new TaskCanceledException(),
        new ArgumentException("permitCount"),
        new ArgumentOutOfRangeException("permitCount"),
        new ObjectDisposedException("limiter"),
        new InvalidOperationException("limiter already disposed"),
    ];

    [Theory]
    [MemberData(nameof(StoreFailures))]
    public void Treats_store_problems_as_failures(Exception exception) =>
        Assert.True(StoreFailureClassifier.IsStoreFailure(exception, shouldHandle: null));

    [Theory]
    [MemberData(nameof(Propagated))]
    public void Propagates_caller_cancellation_and_programming_errors(Exception exception) =>
        Assert.False(StoreFailureClassifier.IsStoreFailure(exception, shouldHandle: null));

    [Fact]
    public void A_null_exception_is_not_a_failure() =>
        Assert.False(StoreFailureClassifier.IsStoreFailure(null, shouldHandle: null));

    [Fact]
    public void A_user_predicate_cannot_reclassify_caller_cancellation()
    {
        Assert.False(StoreFailureClassifier.IsStoreFailure(new OperationCanceledException(), _ => true));
        Assert.False(StoreFailureClassifier.IsStoreFailure(new TaskCanceledException(), _ => true));
    }

    [Fact]
    public void A_user_predicate_replaces_the_built_in_classification()
    {
        var shouldHandle = (Exception exception) => exception is InvalidOperationException;

        Assert.True(StoreFailureClassifier.IsStoreFailure(new InvalidOperationException(), shouldHandle));
        Assert.False(StoreFailureClassifier.IsStoreFailure(new IOException(), shouldHandle));
    }
}
