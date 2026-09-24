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
    /// Decides per request whether to tell this caller that limiting is running on local state instead of the
    /// shared store. Checked only when the request was rejected, and only when the lease that rejected it came
    /// from a non-distributed <see cref="LeaseSource"/> (a distributed lease means the store answered normally).
    /// When it returns <see langword="true"/>, the response gets an <c>X-RateLimit-Degraded: true</c> header.
    /// Null (the default) never sends the header.
    /// </param>
    /// <returns>The same <paramref name="options"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>Call this once inside <c>AddRateLimiter</c>, before or after <c>AddPolicy</c>: it sets
    /// <see cref="RateLimiterOptions.RejectionStatusCode"/> and <see cref="RateLimiterOptions.OnRejected"/>, and does
    /// not touch any policy. Calling it more than once, or after code that already set
    /// <see cref="RateLimiterOptions.OnRejected"/>, silently discards the earlier callback; there is only ever one
    /// <c>OnRejected</c>.</para>
    /// <para>The <c>Retry-After</c> header is written only when the lease reports a
    /// <see cref="MetadataName.RetryAfter"/> value. The value is rounded up to a whole number of seconds (a
    /// 1.2-second wait is reported as 2), never reported as negative, and capped at
    /// <see cref="int.MaxValue"/> seconds.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddRateLimiter(limiterOptions =>
    /// {
    ///     limiterOptions.UseResilientDefaults(emitDegradedHeader: context =>
    ///         context.Connection.RemoteIpAddress is { } ip &amp;&amp; IPAddress.IsLoopback(ip));
    ///
    ///     limiterOptions.AddPolicy("per-client", context => /* ... */);
    /// });
    /// </code>
    /// </example>
    public static RateLimiterOptions UseResilientDefaults(
        this RateLimiterOptions options,
        Func<HttpContext, bool>? emitDegradedHeader = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.OnRejected = (context, _) =>
        {
            if (emitDegradedHeader is not null
                && context.Lease.TryGetMetadata(ResilientRateLimitLease.SourceMetadata, out var source)
                && source != LeaseSource.Distributed
                && emitDegradedHeader(context.HttpContext))
            {
                context.HttpContext.Response.Headers["X-RateLimit-Degraded"] = "true";
            }

            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter.Name, out var metadata)
                && metadata is TimeSpan retryAfter)
            {
                var wholeSeconds = Math.Ceiling(retryAfter.TotalSeconds);
                var seconds = wholeSeconds >= int.MaxValue ? int.MaxValue : Math.Max(0, (int)wholeSeconds);

                context.HttpContext.Response.Headers.RetryAfter =
                    seconds.ToString(NumberFormatInfo.InvariantInfo);
            }

            return ValueTask.CompletedTask;
        };

        return options;
    }
}
