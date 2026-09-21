using Polly;
using Polly.CircuitBreaker;
using System.Threading.RateLimiting;

namespace ResilientRateLimiting;

/// <summary>The health of one shared store. Create one per store connection and share it across partitions.</summary>
public sealed class StoreHealth : IDisposable
{
    private readonly ResiliencePipeline<RateLimitLease> _pipeline;
    private int _livePartitions;

    /// <param name="options">The same options the partitions use.</param>
    /// <param name="timeProvider">Defaults to <see cref="TimeProvider.System"/>.</param>
    public StoreHealth(ResilientRateLimiterOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var shouldHandle = options.ShouldHandle;

        _pipeline = new ResiliencePipelineBuilder<RateLimitLease> { TimeProvider = timeProvider ?? TimeProvider.System }
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<RateLimitLease>
            {
                ShouldHandle = args =>
                    new ValueTask<bool>(StoreFailureClassifier.IsStoreFailure(args.Outcome.Exception, shouldHandle)),
                FailureRatio = 1.0,
                MinimumThroughput = options.FailuresBeforeOpen,
                SamplingDuration = options.BreakerSamplingDuration,
                BreakDuration = options.BreakDuration,
            })
            .Build();
    }

    internal ResiliencePipeline<RateLimitLease> Pipeline => _pipeline;

    internal int LivePartitions => Volatile.Read(ref _livePartitions);

    internal void RegisterPartition() => Interlocked.Increment(ref _livePartitions);

    internal void ReleasePartition() => Interlocked.Decrement(ref _livePartitions);

    /// <summary>No-op: the pipeline holds no unmanaged resource.</summary>
    public void Dispose()
    {
    }
}
