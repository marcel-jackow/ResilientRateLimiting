using Polly;
using Polly.CircuitBreaker;
using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace ResilientRateLimiting;

/// <summary>The health of one shared store. Create one per store connection and share it across partitions.</summary>
public sealed class StoreHealth
{
    private readonly ResiliencePipeline<RateLimitLease> _pipeline;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<Type, long> _lastSeen = new();
    private long _lastClosedAt = long.MinValue;
    private int _livePartitions;
    private bool _reached;

    /// <param name="options">Configuration for this store connection, validated here so a wrong setup fails at startup.</param>
    /// <param name="timeProvider">Defaults to <see cref="TimeProvider.System"/>.</param>
    public StoreHealth(StoreHealthOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        Options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _pipeline = new ResiliencePipelineBuilder<RateLimitLease> { TimeProvider = _timeProvider }
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<RateLimitLease>
            {
                ShouldHandle = args => new ValueTask<bool>(IsStoreFailure(args.Outcome.Exception)),
                FailureRatio = options.FailureRatio,
                MinimumThroughput = options.FailuresBeforeOpen,
                SamplingDuration = options.BreakerSamplingDuration,
                BreakDuration = options.BreakDuration,
                OnClosed = args =>
                {
                    Interlocked.Exchange(ref _lastClosedAt, _timeProvider.GetTimestamp());
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

    /// <summary>Invokes <see cref="StoreHealthOptions.OnStoreFailure"/> for the first failure of this exception type, and again once quiet or closed. Never throws: the callback is caller code, and a handled store failure must not become an unhandled one.</summary>
    internal void ReportFirstOccurrence(Exception exception)
    {
        if (Options.OnStoreFailure is not { } callback || !ShouldReport(exception.GetType(), _timeProvider.GetTimestamp()))
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

    // CAS loop: the winner of TryAdd (first sighting) always reports; otherwise decide from the
    // previous lastSeen before publishing the new one, retrying if another thread raced us.
    private bool ShouldReport(Type type, long now)
    {
        while (true)
        {
            if (_lastSeen.TryAdd(type, now))
            {
                return true;
            }

            if (!_lastSeen.TryGetValue(type, out var lastSeen))
            {
                continue;
            }

            var report = lastSeen < Volatile.Read(ref _lastClosedAt)
                || _timeProvider.GetElapsedTime(lastSeen, now) >= Options.BreakerSamplingDuration;

            if (_lastSeen.TryUpdate(type, now, lastSeen))
            {
                return report;
            }
        }
    }
}
