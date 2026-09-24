# Telemetry

This page is about watching the library from outside: the metrics it records, the store failure reports it can push to your logs, and the two ways your own request-handling code can find out which path answered a request. If a word is new, its full explanation is in the [glossary](01-concepts.md#glossary) in [01-concepts.md](01-concepts.md).

## Metrics

**Problem.** Without any signal from inside the library, you cannot tell a healthy system from one that is quietly running on its [local fallback](01-concepts.md#local-fallback) for every request. Both look the same from the outside: requests still get a lease, most of them are still allowed. The only difference is that the shared store has stopped being asked, and every replica may now be counting independently.

**What goes wrong without it.** An outage on the shared store can run for hours before anyone notices, because nothing failed loudly — clients kept getting responses, just from a less accurate counter. By the time someone notices (a burst of complaints about inconsistent limits between replicas), the outage is long over and there is no record of when it started or how often it has happened since.

**What the library does.** Every [`ResilientRateLimiter`](03-api-reference.md#resilientratelimiter) records two counters on one [`Meter`](https://learn.microsoft.com/dotnet/api/system.diagnostics.metrics.meter), named by the constant [`ResilientRateLimiter.MeterName`](03-api-reference.md#metername) (`"ResilientRateLimiting"`). Subscribe to it through OpenTelemetry (an open standard and set of libraries for collecting metrics, logs, and traces)'s `AddMeter`, or through any other listener built on `System.Diagnostics.Metrics`:

<!-- snippet: web-meter -->
```csharp
builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter(ResilientRateLimiter.MeterName));
```
From `samples/ResilientRateLimiting.Samples.Web/Program.cs`

Both counters are `Counter<long>`, meaning they only ever go up; you read rates and shares from them over a time window, not a current level.

**`resilient_rate_limiting.leases`** — one [lease](01-concepts.md#lease) decision, recorded on every `AcquireAsync` call (not on `AttemptAcquire`, which never carries a source tag).

| Tag | Values | Meaning |
|---|---|---|
| `policy` | the [`PolicyName`](04-configuration.md#policyname) you set | Which policy produced this lease. |
| `outcome` | `allowed`, `limited` | Whether the request got the permit it asked for. |
| `source` | `distributed`, `local_fallback`, `fail_open`, `fail_closed`, `recovery` | Which path answered. See [`LeaseSource`](03-api-reference.md#leasesource) and [Reading the source in your own code](#reading-the-source-in-your-own-code) below. |
| `partition_key` | the partition's key, as text | Present only when [`TagMetricsByPartitionKey`](04-configuration.md#tagmetricsbypartitionkey-and-partitionkey) is `true` and `PartitionKey` has a value. |

Unit: `{lease}` (a UCUM annotation meaning "count of leases", not a physical unit).

**`resilient_rate_limiting.store_failures`** — one [store failure](01-concepts.md#store-failure), recorded each time the [circuit breaker](01-concepts.md#circuit-breaker) classifies an exception from the store limiter as a failure, whether or not that failure is also pushed to [`OnStoreFailure`](#store-failure-reports) below.

| Tag | Values | Meaning |
|---|---|---|
| `policy` | the [`PolicyName`](04-configuration.md#policyname) you set | Which policy's store call failed. |
| `exception_type` | the exception's short type name, for example `RedisConnectionException` | `exception.GetType().Name` — never the namespace-qualified name, and never the exception's message (the message can carry a connection string, a key value, or other request-specific text). |

Unit: `{failure}`.

**Example.** In [Getting started](02-getting-started.md), the ASP.NET Core sample wires `AddMeter` once at startup (the snippet above) and never touches these counters directly — every `AcquireAsync` call on every policy feeds them automatically. Point Grafana, Prometheus, or any other OpenTelemetry-compatible backend at the same meter and the two counters are already there.

## Useful alerts

Two questions are worth an alert on almost any deployment. Neither needs a real limit value to be useful — describe them in words first, then decide the exact threshold from your own traffic:

- **A rising share of non-distributed leases.** If the share of `resilient_rate_limiting.leases` with `source` other than `distributed` climbs and stays up, the store connection is degraded or down; every reply is coming from the [local fallback](01-concepts.md#local-fallback), a fail-open default, or a fail-closed rejection instead of the shared counter.
- **Any store failures at all.** `resilient_rate_limiting.store_failures` should normally sit at zero. A count above zero in a short window, even a small one, is worth looking at — it is the earliest signal an outage exists, well before it is large enough to change the leases share above.

An example, in words rather than a specific query language, so it stays useful whatever backend you read these metrics with: "over the last five minutes, the count of `resilient_rate_limiting.leases` with `source != distributed`, divided by the count of all `resilient_rate_limiting.leases`, for a given `policy`." Turning that into a real alert rule is specific to your metrics backend; this page only tells you which counters and tags to reach for.

## Store failure reports

**Problem.** A metric counter tells you *that* store failures are happening and roughly how many, but not *what* is failing — you cannot page someone with "count went up," and you cannot always wait for a dashboard to be checked.

**What goes wrong without it.** Without a direct report, finding out which exception is behind an outage means digging through request traces or waiting for the metric to be noticed, both slower than a log line or an alert firing the moment the first failure of a new kind happens.

**What the library does.** [`StoreHealthOptions.OnStoreFailure`](03-api-reference.md#storehealthoptions) is a callback you can set to be told about store failures directly. It is not called on every single failure — that would flood your logs during a long outage with the same exception, over and over. Instead, [`StoreHealth`](03-api-reference.md#storehealth) reports:

- the **first** store failure of each distinct exception type it sees, always;
- **again**, once that same exception type has gone quiet for at least [`BreakerSamplingDuration`](04-configuration.md#breakersamplingduration) (no further failure of that type in that window), or once the circuit breaker has closed since the type was last reported — whichever happens first.

A steady stream of the same exception type, while the breaker stays open, is reported once and then left alone until one of those two conditions is met. A different exception type — say a timeout followed later by a connection failure — is reported the first time each type appears, independently of the other.

**Memory.** `StoreHealth` remembers, for each exception *type* it has ever reported, when it last reported that type. This dictionary is keyed by `System.Type`, not by exception instance or message, so it is bounded by the number of distinct exception classes that exist in your program and its dependencies — normally a small, fixed set decided at compile time. It grows without bound only if something in your program creates new `Type` objects at run time (for example, dynamically generated code that defines a new exception class per request), which ordinary applications do not do.

**Never throws.** `OnStoreFailure` is caller code, not library code, and it runs on the request path. If it throws, the exception is caught and dropped inside `StoreHealth` — a failing callback must not turn a handled store failure into an unhandled one.

<!-- snippet: store-failure-callback -->
```csharp
var storeHealth = new StoreHealth(new StoreHealthOptions
{
    OnStoreFailure = exception => Console.WriteLine($"Store failure: {exception.GetType().Name}"),
    ShouldHandle = exception => exception is IOException,
});
```
From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

**`WithLogging`.** [`StoreHealthOptionsExtensions.WithLogging`](03-api-reference.md#withlogging) is a shortcut that logs each reported failure at `Warning`, using an `ILogger` you provide, in addition to any `OnStoreFailure` already set — it does not replace your callback, it wraps it. The log message names only the exception's type; if your logging provider also renders the exception object (message, stack trace), that detail still comes through by way of the exception itself, not the message template.

<!-- snippet: web-with-logging -->
```csharp
// A second store connection: built by hand, so its StoreHealth is built by hand too, with logging attached manually.
var secondaryRedisConnectionString = builder.Configuration.GetConnectionString("RedisSecondary")
    ?? throw new InvalidOperationException("The ConnectionStrings:RedisSecondary configuration value is missing.");

var secondaryRedisOptions = ConfigurationOptions.Parse(secondaryRedisConnectionString);
secondaryRedisOptions.AsyncTimeout = 20;

var secondaryRedis = await ConnectionMultiplexer.ConnectAsync(secondaryRedisOptions);
var secondaryLogger = LoggerFactory.Create(logging => logging.AddConsole())
    .CreateLogger("ResilientRateLimiting.SecondStore");

var secondaryStoreHealth = new StoreHealth(new StoreHealthOptions().WithLogging(secondaryLogger));
```
From `samples/ResilientRateLimiting.Samples.Web/Program.cs`

Calling `WithLogging` twice on the same options wraps the callback twice, so every failure would be logged twice — call it once, right before the options are passed to `StoreHealth`'s constructor.

## Reading the source in your own code

**Problem.** ASP.NET Core's rate limiting middleware, and any other code written against the built-in `System.Threading.RateLimiting` API, works with a plain `RateLimitLease` — it has no idea this library exists, and no property called `Source`.

**What goes wrong without it.** If you need to know, in your own request-handling code, whether a particular reply came from the shared store or from a degraded path, you cannot just cast the lease to a richer type — the middleware and any other caller only ever sees the base `RateLimitLease`, because that is the contract [`ResilientRateLimitLease`](03-api-reference.md#resilientratelimitlease) is built to satisfy.

**What the library does.** `ResilientRateLimitLease` carries its [`LeaseSource`](03-api-reference.md#leasesource) as **metadata** instead of as a settable property on a richer type — metadata is part of the base `RateLimitLease` contract, so any code that only knows about `RateLimitLease` can still read it. The key is [`ResilientRateLimitLease.SourceMetadata`](03-api-reference.md#resilientratelimitlease), a `MetadataName<LeaseSource>`:

<!-- snippet: read-source -->
```csharp
lease.TryGetMetadata(ResilientRateLimitLease.SourceMetadata, out var source);

var description = source switch
{
    LeaseSource.Distributed => "the shared store answered",
    LeaseSource.LocalFallback => "the store failed and the local fallback limiter answered",
    LeaseSource.FailOpen => "the store failed and the request was admitted",
    LeaseSource.FailClosed => "the store failed and the request was rejected",
    LeaseSource.Recovery => "the store just recovered and the local counter answered instead",
    _ => "unknown",
};

Console.WriteLine($"Answered by: {source} ({description})");
```
From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

This is the same tag value recorded on `resilient_rate_limiting.leases` as `source` (see [Metrics](#metrics) above), just read directly from the lease instead of from a metrics backend — useful when you want to act on the source of one specific request, for example to log it, rather than to observe it in aggregate.

## The degraded header

**Problem.** A client that is being rejected because the shared store is down is, in one sense, in the same situation as a client rejected because it is genuinely over its limit — both get a 429. But the two cases are not equally trustworthy: a rejection from the [local fallback](01-concepts.md#local-fallback) is based on one replica's own count, not the shared one, so it can be wrong in ways a rejection from a healthy store cannot.

**What goes wrong without it.** A caller with no way to tell the two cases apart cannot decide whether to treat the rejection as reliable. An internal caller that wants to keep working through an outage (say, a health check or an internal admin tool) has no signal to act on.

**What the library does.** [`UseResilientDefaults`](03-api-reference.md#useresilientdefaults) accepts an optional `emitDegradedHeader` predicate. It is checked only when a request was rejected, and only when the lease that rejected it reports a non-distributed [`LeaseSource`](03-api-reference.md#leasesource) (a distributed lease means the store answered normally, so there is nothing degraded to report). When the predicate returns `true`, the response gets an `X-RateLimit-Degraded: true` header.

<!-- snippet: web-degraded-header -->
```csharp
limiterOptions.UseResilientDefaults(emitDegradedHeader: context =>
    context.Connection.RemoteIpAddress is { } remoteIp && IPAddress.IsLoopback(remoteIp));
```
From `samples/ResilientRateLimiting.Samples.Web/Program.cs`

The example above sends the header only to callers connecting from the loopback address — a stand-in for "internal callers only." The predicate receives the full `HttpContext`, so it can check anything about the request: a header, a claim, the remote address, or nothing at all. Leaving `emitDegradedHeader` as `null` (the default) never sends the header, whatever the lease source.

## Cardinality

**Problem.** Every distinct combination of tag values on a metric becomes its own stored time series in most metrics backends, and most backends never forget a time series once it has appeared, even long after that combination stops occurring. A tag whose values come from user input, rather than from a small fixed list your code controls, can turn one counter into an unbounded, ever-growing set of counters.

**What goes wrong without it.** Tag a metric by, for example, a user ID or an IP address, and every user or address that has ever made a request adds a permanent time series, forever — the metrics backend's storage and query cost both grow without bound, usually far faster than anyone notices until a bill or a query timeout makes it obvious.

**What the library does — and what you control.** Of the four tags on `resilient_rate_limiting.leases`:

- `outcome` and `source` are fixed by the library itself: two and five possible values respectively, never more.
- `policy` is whatever you set as [`PolicyName`](04-configuration.md#policyname) — safe as long as it is one short, fixed value per policy, chosen at startup, never per request. See [`PolicyName`](04-configuration.md#policyname) in [04-configuration.md](04-configuration.md) for what goes wrong when it is not.
- `partition_key`, added only when [`TagMetricsByPartitionKey`](04-configuration.md#tagmetricsbypartitionkey-and-partitionkey) is `true`, carries whatever value your partition key is. This is the tag most likely to cause a cardinality problem, because partition keys are often exactly the kind of per-caller value — an IP address, a user ID, a tenant ID with many tenants — that this section warns about. Turn `TagMetricsByPartitionKey` on only for a partition key drawn from a small, fixed set of values you would be comfortable seeing as separate, permanent time series.

`resilient_rate_limiting.store_failures`' `exception_type` tag is bounded by the exception classes that exist in your program and its dependencies (see [Memory](#store-failure-reports) above) — in practice a small, fixed set, and not something you need to guard against the way you do `partition_key`.

The same rule applies beyond this library's own metrics: before adding a tag to any metric, ask whether its set of possible values is small and fixed, or grows with your traffic. Only the first kind belongs on a metric tag.

Next: [06-production.md](06-production.md) — running this in production: dashboards, dependency and readiness checks, and what to watch during an outage.
