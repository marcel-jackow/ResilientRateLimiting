using Polly;
using Polly.Timeout;
using System.Threading.RateLimiting;

namespace ResilientRateLimiting;

/// <summary>Wraps a store-backed rate limiter so a slow or unreachable store degrades instead of failing the request.</summary>
public sealed class ResilientRateLimiter : RateLimiter
{
    private readonly RateLimiter _primary;
    private readonly RateLimiter? _fallback;
    private readonly ResilientRateLimiterOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ResiliencePipeline<RateLimitLease> _pipeline;

    /// <param name="primary">The limiter backed by the shared store.</param>
    /// <param name="fallback">In-memory limiter, required for <see cref="StoreFailureBehavior.LocalFallback"/>.</param>
    /// <param name="options">Configuration, validated here so a wrong setup fails at startup.</param>
    /// <param name="timeProvider">Defaults to <see cref="TimeProvider.System"/>.</param>
    public ResilientRateLimiter(
        RateLimiter primary,
        RateLimiter? fallback,
        ResilientRateLimiterOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (options.FailureBehavior == StoreFailureBehavior.LocalFallback)
        {
            ArgumentNullException.ThrowIfNull(fallback);
        }

        _primary = primary;
        _fallback = fallback;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pipeline = BuildPipeline();
    }

    /// <inheritdoc />
    public override TimeSpan? IdleDuration => _primary.IdleDuration;

    /// <summary>Always <see langword="null"/>; see the README.</summary>
    public override RateLimiterStatistics? GetStatistics() => null;

    /// <summary>Always rejects: a network store cannot be consulted synchronously, and the middleware calls the async path next.</summary>
    protected override RateLimitLease AttemptAcquireCore(int permitCount) =>
        new ResilientRateLimitLease(StaticLease.Rejected, LeaseSource.Distributed);

    /// <inheritdoc />
    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        try
        {
            var lease = await _pipeline
                .ExecuteAsync(
                    async token => await _primary.AcquireAsync(permitCount, token).ConfigureAwait(false),
                    cancellationToken)
                .ConfigureAwait(false);

            return new ResilientRateLimitLease(lease, LeaseSource.Distributed);
        }
        catch (Exception exception) when (StoreFailureClassifier.IsStoreFailure(exception, _options))
        {
            // Anything thrown from here on has nowhere left to go, and reaches the caller.
            return await FallbackAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        _primary.Dispose();
        _fallback?.Dispose();
    }

    private ResiliencePipeline<RateLimitLease> BuildPipeline() =>
        new ResiliencePipelineBuilder<RateLimitLease> { TimeProvider = _timeProvider }
            .AddTimeout(new TimeoutStrategyOptions { Timeout = _options.StoreTimeout })
            .Build();

    private async ValueTask<RateLimitLease> FallbackAsync(CancellationToken cancellationToken) =>
        _options.FailureBehavior switch
        {
            StoreFailureBehavior.FailOpen =>
                new ResilientRateLimitLease(StaticLease.Acquired, LeaseSource.FailOpen),

            StoreFailureBehavior.FailClosed =>
                new ResilientRateLimitLease(StaticLease.Rejected, LeaseSource.FailClosed),

            _ => new ResilientRateLimitLease(
                await _fallback!.AcquireAsync(1, cancellationToken).ConfigureAwait(false),
                LeaseSource.LocalFallback),
        };
}
