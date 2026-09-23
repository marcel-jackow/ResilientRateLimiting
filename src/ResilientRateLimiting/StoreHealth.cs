using Polly;
using Polly.CircuitBreaker;
using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace ResilientRateLimiting;

/// <summary>The health of one shared store. Create one per store connection and share it across partitions.</summary>
public sealed class StoreHealth
{
    private readonly ResiliencePipeline<RateLimitLease> _pipeline;
    private readonly ConcurrentDictionary<Type, byte> _reportedFailureTypes = new();
    private int _livePartitions;
    private bool _reached;

    /// <param name="options">Configuration for this store connection, validated here so a wrong setup fails at startup.</param>
    /// <param name="timeProvider">Defaults to <see cref="TimeProvider.System"/>.</param>
    public StoreHealth(StoreHealthOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        Options = options;

        _pipeline = new ResiliencePipelineBuilder<RateLimitLease> { TimeProvider = timeProvider ?? TimeProvider.System }
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<RateLimitLease>
            {
                ShouldHandle = args => new ValueTask<bool>(IsStoreFailure(args.Outcome.Exception)),
                FailureRatio = options.FailureRatio,
                MinimumThroughput = options.FailuresBeforeOpen,
                SamplingDuration = options.BreakerSamplingDuration,
                BreakDuration = options.BreakDuration,
                OnClosed = args =>
                {
                    ForgetReportedFailureTypes();
                    return default;
                },
            })
            .Build();
    }

    /// <summary>The settings every limiter on this connection shares. One copy, so nothing can disagree with it.</summary>
    internal StoreHealthOptions Options { get; }

    internal ResiliencePipeline<RateLimitLease> Pipeline => _pipeline;

    /// <summary>The single verdict on whether an exception means this store failed. The breaker scores outcomes with it, and every limiter on this connection asks it rather than keeping its own predicate.</summary>
    internal bool IsStoreFailure(Exception? exception) =>
        StoreFailureClassifier.IsStoreFailure(exception, Options.ShouldHandle);

    internal int LivePartitions => Volatile.Read(ref _livePartitions);

    /// <summary>Whether any limiter on this store connection has had an answer from the store.</summary>
    internal bool HasBeenReached => Volatile.Read(ref _reached);

    internal void MarkReached() => Volatile.Write(ref _reached, true);

    internal void RegisterPartition() => Interlocked.Increment(ref _livePartitions);

    internal void ReleasePartition() => Interlocked.Decrement(ref _livePartitions);

    /// <summary>Invokes <see cref="StoreHealthOptions.OnStoreFailure"/> the first time this instance sees this exception's type. Never throws: the callback is caller code, and a handled store failure must not become an unhandled one.</summary>
    internal void ReportFirstOccurrence(Exception exception)
    {
        if (Options.OnStoreFailure is not { } callback || !_reportedFailureTypes.TryAdd(exception.GetType(), 0))
        {
            return;
        }

        try
        {
            callback(exception);
        }
        catch
        {
            // See the summary: a throwing callback has nowhere useful to go.
        }
    }

    /// <summary>Clears the reported-types memory so the next outage reports each type again. Called from Polly's OnClosed on the half-open-to-closed transition, i.e. once per outage, not on every successful store call.</summary>
    private void ForgetReportedFailureTypes()
    {
        try
        {
            _reportedFailureTypes.Clear();
        }
        catch
        {
            // Never let a Polly transition callback throw.
        }
    }
}
