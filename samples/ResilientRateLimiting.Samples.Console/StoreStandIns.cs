using ResilientRateLimiting;
using System.Threading.RateLimiting;

// Stands in for a Redis-backed limiter so the sample runs without Redis.
internal sealed class InMemoryStore(int permitLimit, TimeSpan window) : RateLimiter
{
    private readonly FixedWindowRateLimiter _inner = new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = permitLimit,
        Window = window,
        QueueLimit = 0,
    });

    public override TimeSpan? IdleDuration => _inner.IdleDuration;

    public override RateLimiterStatistics? GetStatistics() => _inner.GetStatistics();

    protected override RateLimitLease AttemptAcquireCore(int permitCount) => _inner.AttemptAcquire(permitCount);

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
        _inner.AcquireAsync(permitCount, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
    }
}

// Stands in for a Redis-backed limiter whose store cannot be reached.
internal sealed class UnreachableStore : RateLimiter
{
    public override TimeSpan? IdleDuration => null;

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount) => throw new IOException("store unreachable");

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
        ValueTask.FromException<RateLimitLease>(new IOException("store unreachable"));
}
