using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using System.Globalization;
using System.Threading.RateLimiting;

namespace ResilientRateLimiting.AspNetCore;

/// <summary>Wiring the framework leaves to the caller.</summary>
public static class ResilientRateLimitingOptionsExtensions
{
    /// <summary>
    /// Sets the rejection status to 429 and writes a <c>Retry-After</c> header from the lease.
    /// The framework's own default is 503, and its middleware writes no header at all.
    /// Replaces any <see cref="RateLimiterOptions.OnRejected"/> already set.
    /// </summary>
    /// <param name="options">The middleware options to configure.</param>
    /// <param name="emitDegradedHeader">
    /// Whether to tell the caller that limiting is currently running on local state. Off by
    /// default: an internet-facing API may not want to publish that.
    /// </param>
    /// <returns>The same options, for chaining.</returns>
    public static RateLimiterOptions UseResilientDefaults(
        this RateLimiterOptions options,
        bool emitDegradedHeader = false)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.OnRejected = (context, _) =>
        {
            if (emitDegradedHeader
                && context.Lease.TryGetMetadata(ResilientRateLimitLease.SourceMetadata, out var source)
                && source != LeaseSource.Distributed)
            {
                context.HttpContext.Response.Headers["X-RateLimit-Degraded"] = "true";
            }

            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter.Name, out var metadata)
                && metadata is TimeSpan retryAfter)
            {
                var wholeSeconds = Math.Ceiling(retryAfter.TotalSeconds);
                var seconds = wholeSeconds >= int.MaxValue ? int.MaxValue : (int)wholeSeconds;

                context.HttpContext.Response.Headers.RetryAfter =
                    seconds.ToString(NumberFormatInfo.InvariantInfo);
            }

            return ValueTask.CompletedTask;
        };

        return options;
    }
}
