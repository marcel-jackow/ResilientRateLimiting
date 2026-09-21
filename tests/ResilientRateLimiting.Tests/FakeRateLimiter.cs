using System.Threading.RateLimiting;

namespace ResilientRateLimiting.Tests;

/// <summary>Counts deterministically; fails, hangs or recovers on command.</summary>
public sealed class FakeRateLimiter(int permitLimit = int.MaxValue) : RateLimiter
{
    private readonly Lock _gate = new();
    private int _available = permitLimit;
    private int _remainingFailures;
    private Exception? _failure;
    private TaskCompletionSource? _hang;
    private bool _observeCancellation = true;

    private int _leasesDisposed;

    public int AcquireAttempts { get; private set; }

    public int DisposeCount { get; private set; }

    public int LeasesDisposed => Volatile.Read(ref _leasesDisposed);

    public int AvailablePermits
    {
        get
        {
            lock (_gate)
            {
                return _available;
            }
        }
    }

    public TimeSpan? RetryAfter { get; set; }

    public override TimeSpan? IdleDuration => TimeSpan.Zero;

    public override RateLimiterStatistics? GetStatistics() => null;

    public FakeRateLimiter FailTimes(int count, Exception? failure = null)
    {
        _remainingFailures = count;
        _failure = failure ?? new InvalidDataException("store unavailable");
        return this;
    }

    public FakeRateLimiter AlwaysFail(Exception failure) => FailTimes(int.MaxValue, failure);

    public FakeRateLimiter HangUntilReleased()
    {
        _hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return this;
    }

    public FakeRateLimiter HangIgnoringCancellation()
    {
        _hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _observeCancellation = false;
        return this;
    }

    public void Release() => _hang?.TrySetResult();

    protected override void Dispose(bool disposing)
    {
        DisposeCount++;
        base.Dispose(disposing);
    }

    protected override RateLimitLease AttemptAcquireCore(int permitCount) => Take(permitCount);

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        AcquireAttempts++;

        if (_hang is { } hang)
        {
            if (_observeCancellation)
            {
                await hang.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await hang.Task.ConfigureAwait(false);
            }
        }

        if (_remainingFailures > 0)
        {
            _remainingFailures--;
            throw _failure!;
        }

        return Take(permitCount);
    }

    private RateLimitLease Take(int permitCount)
    {
        lock (_gate)
        {
            if (_available < permitCount)
            {
                return new FakeLease(this, false, RetryAfter);
            }

            _available -= permitCount;
            return new FakeLease(this, true, null);
        }
    }

    private sealed class FakeLease(FakeRateLimiter owner, bool acquired, TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => acquired;

        public override IEnumerable<string> MetadataNames =>
            retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (metadataName == MetadataName.RetryAfter.Name && retryAfter is { } value)
            {
                metadata = value;
                return true;
            }

            metadata = null;
            return false;
        }

        protected override void Dispose(bool disposing) => Interlocked.Increment(ref owner._leasesDisposed);
    }
}
