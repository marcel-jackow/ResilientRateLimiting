using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Primitives;
using System.Threading.RateLimiting;
using Xunit;

namespace ResilientRateLimiting.AspNetCore.Tests;

public class RejectionTests
{
    private sealed class RejectedLease(TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => false;

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
    }

    [Fact]
    public void Defaults_the_rejection_status_to_429()
    {
        var options = new RateLimiterOptions();

        options.UseResilientDefaults();

        Assert.Equal(StatusCodes.Status429TooManyRequests, options.RejectionStatusCode);
    }

    [Fact]
    public async Task Writes_a_retry_after_header_from_the_lease()
    {
        var options = new RateLimiterOptions();
        options.UseResilientDefaults();

        var context = new DefaultHttpContext();
        var rejection = new OnRejectedContext
        {
            HttpContext = context,
            Lease = new RejectedLease(TimeSpan.FromSeconds(42)),
        };

        await options.OnRejected!(rejection, CancellationToken.None);

        Assert.Equal("42", context.Response.Headers.RetryAfter);
    }

    [Fact]
    public async Task Writes_no_header_when_the_lease_has_no_value()
    {
        var options = new RateLimiterOptions();
        options.UseResilientDefaults();

        var context = new DefaultHttpContext();
        var rejection = new OnRejectedContext
        {
            HttpContext = context,
            Lease = new RejectedLease(null),
        };

        await options.OnRejected!(rejection, CancellationToken.None);

        Assert.True(StringValues.IsNullOrEmpty(context.Response.Headers.RetryAfter));
    }

    [Fact]
    public async Task Rounds_a_fractional_value_up()
    {
        var options = new RateLimiterOptions();
        options.UseResilientDefaults();

        var context = new DefaultHttpContext();
        var rejection = new OnRejectedContext
        {
            HttpContext = context,
            Lease = new RejectedLease(TimeSpan.FromMilliseconds(1200)),
        };

        await options.OnRejected!(rejection, CancellationToken.None);

        Assert.Equal("2", context.Response.Headers.RetryAfter);
    }

    [Fact]
    public async Task Clamps_a_negative_retry_after_to_zero()
    {
        var options = new RateLimiterOptions();
        options.UseResilientDefaults();

        var context = new DefaultHttpContext();
        var rejection = new OnRejectedContext
        {
            HttpContext = context,
            Lease = new RejectedLease(TimeSpan.FromSeconds(-5)),
        };

        await options.OnRejected!(rejection, CancellationToken.None);

        Assert.Equal("0", context.Response.Headers.RetryAfter);
    }

    [Fact]
    public async Task Saturates_instead_of_throwing_for_a_huge_retry_after()
    {
        var options = new RateLimiterOptions();
        options.UseResilientDefaults();

        var context = new DefaultHttpContext();
        var rejection = new OnRejectedContext
        {
            HttpContext = context,
            Lease = new RejectedLease(TimeSpan.MaxValue),
        };

        await options.OnRejected!(rejection, CancellationToken.None);

        Assert.Equal(int.MaxValue.ToString(), context.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task Writes_the_degraded_header_only_when_opted_in()
    {
        var options = new RateLimiterOptions();
        options.UseResilientDefaults(emitDegradedHeader: _ => true);

        var context = new DefaultHttpContext();
        using var degradedLease = new ResilientRateLimitLease(new RejectedLease(null), LeaseSource.LocalFallback);

        await options.OnRejected!(
            new OnRejectedContext { HttpContext = context, Lease = degradedLease },
            CancellationToken.None);

        Assert.Equal("true", context.Response.Headers["X-RateLimit-Degraded"]);
    }

    [Fact]
    public async Task Writes_no_degraded_header_by_default()
    {
        var options = new RateLimiterOptions();
        options.UseResilientDefaults();

        var context = new DefaultHttpContext();
        using var degradedLease = new ResilientRateLimitLease(new RejectedLease(null), LeaseSource.LocalFallback);

        await options.OnRejected!(
            new OnRejectedContext { HttpContext = context, Lease = degradedLease },
            CancellationToken.None);

        Assert.True(StringValues.IsNullOrEmpty(context.Response.Headers["X-RateLimit-Degraded"]));
    }

    [Fact]
    public async Task Writes_no_degraded_header_when_the_predicate_is_explicitly_null()
    {
        var options = new RateLimiterOptions();
        options.UseResilientDefaults(emitDegradedHeader: null);

        var context = new DefaultHttpContext();
        using var degradedLease = new ResilientRateLimitLease(new RejectedLease(null), LeaseSource.LocalFallback);

        await options.OnRejected!(
            new OnRejectedContext { HttpContext = context, Lease = degradedLease },
            CancellationToken.None);

        Assert.True(StringValues.IsNullOrEmpty(context.Response.Headers["X-RateLimit-Degraded"]));
    }

    [Fact]
    public async Task Writes_no_degraded_header_when_the_predicate_returns_false()
    {
        var options = new RateLimiterOptions();
        options.UseResilientDefaults(emitDegradedHeader: _ => false);

        var context = new DefaultHttpContext();
        using var degradedLease = new ResilientRateLimitLease(new RejectedLease(null), LeaseSource.LocalFallback);

        await options.OnRejected!(
            new OnRejectedContext { HttpContext = context, Lease = degradedLease },
            CancellationToken.None);

        Assert.True(StringValues.IsNullOrEmpty(context.Response.Headers["X-RateLimit-Degraded"]));
    }

    [Fact]
    public async Task The_predicate_inspects_the_request_and_only_fires_for_a_matching_one()
    {
        var options = new RateLimiterOptions();
        options.UseResilientDefaults(
            emitDegradedHeader: ctx => ctx.Request.Headers["X-Internal"] == "1");

        var internalContext = new DefaultHttpContext();
        internalContext.Request.Headers["X-Internal"] = "1";
        using var internalLease = new ResilientRateLimitLease(new RejectedLease(null), LeaseSource.LocalFallback);

        await options.OnRejected!(
            new OnRejectedContext { HttpContext = internalContext, Lease = internalLease },
            CancellationToken.None);

        Assert.Equal("true", internalContext.Response.Headers["X-RateLimit-Degraded"]);

        var externalContext = new DefaultHttpContext();
        using var externalLease = new ResilientRateLimitLease(new RejectedLease(null), LeaseSource.LocalFallback);

        await options.OnRejected!(
            new OnRejectedContext { HttpContext = externalContext, Lease = externalLease },
            CancellationToken.None);

        Assert.True(StringValues.IsNullOrEmpty(externalContext.Response.Headers["X-RateLimit-Degraded"]));
    }

    [Fact]
    public async Task A_recovery_sourced_lease_counts_as_degraded()
    {
        var options = new RateLimiterOptions();
        options.UseResilientDefaults(emitDegradedHeader: _ => true);

        var context = new DefaultHttpContext();
        using var recoveryLease = new ResilientRateLimitLease(new RejectedLease(null), LeaseSource.Recovery);

        await options.OnRejected!(
            new OnRejectedContext { HttpContext = context, Lease = recoveryLease },
            CancellationToken.None);

        Assert.Equal("true", context.Response.Headers["X-RateLimit-Degraded"]);
    }

    [Fact]
    public async Task A_distributed_sourced_lease_never_gets_the_header_even_when_opted_in()
    {
        var options = new RateLimiterOptions();
        options.UseResilientDefaults(emitDegradedHeader: _ => true);

        var context = new DefaultHttpContext();
        using var distributedLease = new ResilientRateLimitLease(new RejectedLease(null), LeaseSource.Distributed);

        await options.OnRejected!(
            new OnRejectedContext { HttpContext = context, Lease = distributedLease },
            CancellationToken.None);

        Assert.True(StringValues.IsNullOrEmpty(context.Response.Headers["X-RateLimit-Degraded"]));
    }

    [Fact]
    public async Task The_predicate_is_not_called_for_a_distributed_sourced_lease()
    {
        var calls = 0;
        var options = new RateLimiterOptions();
        options.UseResilientDefaults(emitDegradedHeader: _ =>
        {
            calls++;
            return true;
        });

        var context = new DefaultHttpContext();
        using var distributedLease = new ResilientRateLimitLease(new RejectedLease(null), LeaseSource.Distributed);

        await options.OnRejected!(
            new OnRejectedContext { HttpContext = context, Lease = distributedLease },
            CancellationToken.None);

        Assert.Equal(0, calls);
    }
}
