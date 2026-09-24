# Getting started

This page is a walk from an empty console app to an ASP.NET Core app with Redis. Every step runs, and every output shown here is real output from running that step. If a term is new, it is **bold** on first use and links to the [glossary in 01-concepts.md](01-concepts.md#glossary); read that page first if you have never used a rate limiter before.

## Before you start

You need:

- **.NET 10** (the SDK the samples target).
- **Docker**, to run Redis. You do not need it until [Step 4](#step-4-an-aspnet-core-app-with-redis); steps 1 to 3 use small in-process stand-ins for a store, so they need no Docker and no network.

The three packages you will add to a real project:

```bash
dotnet add package ResilientRateLimiting
dotnet add package RedisRateLimiting
dotnet add package ResilientRateLimiting.AspNetCore
```

`ResilientRateLimiting` is the core package (the resilience wrapper). `RedisRateLimiting` is a separate, open-source package that does the actual counting in Redis; see [RedisRateLimiting](01-concepts.md#redisratelimiting). `ResilientRateLimiting.AspNetCore` is only for ASP.NET Core apps, used from [Step 4](#step-4-an-aspnet-core-app-with-redis) on.

These packages are not on nuget.org yet (this library is at version 0.1.0, unpublished). Until the first release, add a project reference to this repository's `src/ResilientRateLimiting` and `src/ResilientRateLimiting.AspNetCore` projects instead of the `dotnet add package` commands above.

This page quotes the two sample projects in this repository: `samples/ResilientRateLimiting.Samples.Console` and `samples/ResilientRateLimiting.Samples.Web`. Clone the repository if you want to run the same commands.

## Step 1: a limiter in a console app, no Redis

Here is the smallest complete example: one **[rate limiter](01-concepts.md#rate-limiter)** backed by Redis, with resilience added.

<!-- snippet: quick-start -->
```csharp
var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
var storeHealth = new StoreHealth(new StoreHealthOptions());
var options = new ResilientRateLimiterOptions { FallbackRecoveryTime = TimeSpan.FromMinutes(1) };

var primary = new RedisSlidingWindowRateLimiter<string>("orders-api", new RedisSlidingWindowRateLimiterOptions
{
    PermitLimit = 100,
    Window = TimeSpan.FromMinutes(1),
    ConnectionMultiplexerFactory = () => redis,
});

var fallback = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
{
    PermitLimit = LocalBudget.ForReplicas(sharedPermitLimit: 100, replicaCount: 3),
    Window = TimeSpan.FromMinutes(1),
    QueueLimit = 0,
});

using var limiter = primary.WithResilience(fallback, options, storeHealth);
using var lease = await limiter.AcquireAsync(permitCount: 1);

Console.WriteLine(lease.IsAcquired ? "Allowed" : "Rejected");
```
From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

Line by line:

- `ConnectionMultiplexer.ConnectAsync("localhost:6379")` opens the connection to [Redis](01-concepts.md#redis), using the `StackExchange.Redis` client. `RedisRateLimiting` needs this connection to talk to Redis.
- `StoreHealth` is the object that watches the [store](01-concepts.md#store) (here, Redis) across every limiter that shares it: it holds the **[store timeout](01-concepts.md#store-timeout)** and the **[circuit breaker](01-concepts.md#circuit-breaker)**. `StoreHealthOptions` here uses its defaults (20 ms timeout, breaker opens after 5 of the last 10 seconds' calls fail with at least half failing). Create one `StoreHealth` per store connection, and pass it to every limiter that uses that connection; [The circuit breaker](01-concepts.md#the-circuit-breaker) explains why.
- `ResilientRateLimiterOptions.FallbackRecoveryTime` must be set whenever a [local fallback](01-concepts.md#local-fallback) is used (it is, by default). It is the time the fallback limiter needs to refill from empty; here the fallback is a one-minute window, so one minute. [Recovery after an outage](01-concepts.md#recovery-after-an-outage) explains what it is used for.
- `primary` is the limiter that does the real counting: a `RedisSlidingWindowRateLimiter<string>` from the `RedisRateLimiting` package, one of the four Redis-backed limiters mentioned in [RedisRateLimiting](01-concepts.md#redisratelimiting). It behaves like any other `RateLimiter`: this library never calls Redis directly.
- `fallback` is the limiter that answers while Redis cannot. `LocalBudget.ForReplicas(sharedPermitLimit: 100, replicaCount: 3)` computes the **[local budget](01-concepts.md#local-budget)**: 100 divided by 3 typical replicas, rounded up to 34. See [The local budget](01-concepts.md#the-local-budget) for why you pass the typical replica count, not the maximum.
- `primary.WithResilience(fallback, options, storeHealth)` builds the actual `ResilientRateLimiter`: an extension method on any `RateLimiter`, so `primary` becomes the thing the rest of your code calls. Passing `fallback: null` instead (see [Step 2](#step-2-see-the-fallback-work)) means "fail open or fail closed instead of a local fallback".
- `limiter.AcquireAsync(permitCount: 1)` asks for one **[permit](01-concepts.md#permit)**. The answer is a **[lease](01-concepts.md#lease)**: `lease.IsAcquired` is `true` or `false`. Always dispose the lease (the `using var` does this) once you are done with the request it guarded.

**Run it.** Start Redis, then run the console sample with `--redis`:

```bash
docker run -d --name rrl-getting-started-redis -p 6379:6379 redis:7
dotnet run --project samples/ResilientRateLimiting.Samples.Console -- --redis
```

The first block of real output from this run:

```text
== quick-start ==
Allowed
```

Without `--redis`, the console sample skips this one scenario (it prints a note explaining why) and runs every other scenario below, none of which needs Redis:

```text
== quick-start ==
Skipped: pass --redis with a Redis server on localhost:6379 to run it.
```

The rest of the console sample uses small stand-in classes instead of real Redis (`InMemoryStore`, which behaves like a small in-memory Redis, and `UnreachableStore`, which always fails) so the remaining steps on this page run with plain `dotnet run --project samples/ResilientRateLimiting.Samples.Console`, no Docker required.

## Step 2: see the fallback work

[When the shared store is slow or down](01-concepts.md#when-the-shared-store-is-slow-or-down) lists three answers to a **[store failure](01-concepts.md#store-failure)**: **[fail open](01-concepts.md#fail-open)**, **[fail closed](01-concepts.md#fail-closed)**, and local fallback. The console sample's `FailOpen` and `FailClosed` scenarios use `UnreachableStore`, a fake primary that always throws, so the behaviour is visible without waiting for a real timeout:

<!-- snippet: fail-open -->
```csharp
var options = new ResilientRateLimiterOptions { FailureBehavior = StoreFailureBehavior.FailOpen };

using var limiter = primary.WithResilience(fallback: null, options, storeHealth);
```
From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

<!-- snippet: fail-closed -->
```csharp
var options = new ResilientRateLimiterOptions { FailureBehavior = StoreFailureBehavior.FailClosed };

using var limiter = primary.WithResilience(fallback: null, options, storeHealth);
```
From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

`fallback: null` is only valid with `FailOpen` or `FailClosed`: there is nothing for a local counter to fall back to, so none is built. Running the console sample prints:

```text
== fail-open ==
Allowed: True

== fail-closed ==
Allowed: False
```

Every store failure. `FailOpen` admits the request even though the store never answered. `FailClosed` rejects it even though the service itself is healthy. Neither of them uses a local counter, so the [source tag](01-concepts.md#source-tag) on the lease tells you which path answered:

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

This scenario's primary is healthy, so with a real Redis running, the output is:

```text
== read-source ==
Answered by: Distributed (the shared store answered)
```

`source` is a `LeaseSource` value read from the lease's metadata with `TryGetMetadata`, the same pattern .NET uses for `MetadataName.RetryAfter`. Your code, your logs and your metrics can all tell a normal answer (`Distributed`) from a degraded one (`LocalFallback`, `FailOpen`, `FailClosed`, `Recovery`). [Step 4](#step-4-an-aspnet-core-app-with-redis) shows `LocalFallback` for real, against a Redis container you stop yourself.

## Step 3: many clients — partitions

One limiter for the whole service punishes every client for one noisy client's traffic. A **[partition](01-concepts.md#partition)** is a separate counter per **[partition key](01-concepts.md#partition-key)** (a client ID, a tenant ID, an API key — whatever identifies the caller). This library's helper for that is `ResilientRateLimitPartition.Get`:

<!-- snippet: partitioned -->
```csharp
using var limiter = PartitionedRateLimiter.Create<string, string>(key =>
    ResilientRateLimitPartition.Get(
        key,
        partitionKey => new InMemoryStore(permitLimit: 5, window: TimeSpan.FromSeconds(10)),
        partitionKey => new InMemoryStore(permitLimit: 2, window: TimeSpan.FromSeconds(10)),
        options,
        storeHealth));
```
From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

`PartitionedRateLimiter.Create` is the built-in .NET type; you give it a function from a key to a partition. `ResilientRateLimitPartition.Get` is this library's partition factory: it takes the key, a function that builds the primary limiter for that key, a function that builds the fallback limiter for that key, the shared `ResilientRateLimiterOptions`, and the shared `StoreHealth`. `.NET` builds each key's pair of limiters (primary and fallback) the first time that key is seen, and keeps them for later requests with the same key. Running the console sample:

```text
== partitioned ==
Allowed: True
```

Each replica still needs a **[local budget](01-concepts.md#local-budget)** per partition, sized from the shared limit for that partition, not the whole service:

<!-- snippet: local-budget -->
```csharp
var localLimit = LocalBudget.ForReplicas(sharedPermitLimit: 100, replicaCount: 3);

using var fallback = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
{
    PermitLimit = localLimit,
    Window = TimeSpan.FromMinutes(1),
    QueueLimit = 0,
});
```
From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

```text
== local-budget ==
Local budget per replica: 34
```

100 divided by 3 replicas is 33.3, rounded up to 34: one replica alone can admit 34 requests for one client during an outage before its local counter for that client refuses. [Step 4](#step-4-an-aspnet-core-app-with-redis) uses this exact helper, with the `X-Client-Id` request header as the partition key.

## Step 4: an ASP.NET Core app with Redis

The web sample (`samples/ResilientRateLimiting.Samples.Web`) is a minimal ASP.NET Core app: one endpoint, rate-limited per client, backed by Redis, with `ResilientRateLimiting.AspNetCore` doing the wiring.

**Configuration.** `appsettings.json`:

<!-- snippet: file:samples/ResilientRateLimiting.Samples.Web/appsettings.json -->
```json
{
  "ConnectionStrings": {
    "Redis": "localhost:6379",
    "RedisSecondary": "localhost:6379"
  },
  "ResilientRateLimiting": {
    "Store": {
      "StoreTimeout": "00:00:00.020",
      "FailuresBeforeOpen": 5,
      "FailureRatio": 0.5,
      "BreakDuration": "00:00:05",
      "BreakerSamplingDuration": "00:00:10",
      "MaxWarmPartitions": 10000,
      "ColdStartFallbackFactor": 1.0
    }
  },
  "RateLimits": {
    "PermitLimit": 100,
    "WindowSeconds": 60,
    "TypicalReplicaCount": 3
  }
}
```
From `samples/ResilientRateLimiting.Samples.Web/appsettings.json`

`ConnectionStrings:Redis` and `ConnectionStrings:RedisSecondary` are plain ASP.NET Core connection strings (the sample opens two Redis connections to show that `StoreHealth` is per connection; this page only uses the first one). `ResilientRateLimiting:Store` binds straight to `StoreHealthOptions`: these are the same settings you saw built by hand in [Step 1](#step-1-a-limiter-in-a-console-app-no-redis) (**starting points**, not measured recommendations; [04-configuration.md](04-configuration.md) explains each one). `RateLimits` is not a type this library knows about; it is the sample's own settings, read into a small `record` and used to build `ResilientRateLimiterOptions` and the Redis and fallback limiters in code below.

**Wiring the shared parts.** `AddResilientRateLimiting` registers a `StoreHealth` singleton, built from that configuration section:

<!-- snippet: web-services -->
```csharp
builder.Services.AddSingleton(redis);
builder.Services.AddResilientRateLimiting(
    builder.Configuration.GetSection("ResilientRateLimiting:Store"),
    Configure);
```
From `samples/ResilientRateLimiting.Samples.Web/Program.cs`

The connection multiplexer itself (`redis`) is registered as a singleton too, but the policy below does not resolve it from DI: it is a local variable in `Program.cs`, so the policy's lambda simply captures it by closure (`ConnectionMultiplexerFactory = () => redis`). Only `StoreHealth` is resolved from `context.RequestServices` inside the policy, because it must be the one shared instance for the connection, and DI is how that instance reaches code running per request. `AddResilientRateLimiting` also validates the bound options on startup and wires up `ValidateOnStart`, so a mistake in `appsettings.json` (for example a `BreakDuration` of zero) fails fast instead of only showing up once the store first misbehaves.

**The policy.** ASP.NET Core's own rate limiting middleware (`AddRateLimiter`, `UseRateLimiter`) is unchanged; this library only supplies the partition and the defaults:

<!-- snippet: web-policy -->
```csharp
limiterOptions.AddPolicy("per-client", context =>
{
    var clientId = context.Request.Headers["X-Client-Id"].FirstOrDefault() ?? "anonymous";
    var storeHealth = context.RequestServices.GetRequiredService<StoreHealth>();

    return ResilientRateLimitPartition.Get(
        clientId,
        key => new RedisSlidingWindowRateLimiter<string>(key, new RedisSlidingWindowRateLimiterOptions
        {
            PermitLimit = rateLimits.PermitLimit,
            Window = TimeSpan.FromSeconds(rateLimits.WindowSeconds),
            ConnectionMultiplexerFactory = () => redis,
        }),
        key => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = localPermitLimit,
            Window = TimeSpan.FromSeconds(rateLimits.WindowSeconds),
            QueueLimit = 0,
        }),
        policyOptions,
        storeHealth);
});
```
From `samples/ResilientRateLimiting.Samples.Web/Program.cs`

`clientId` comes from the `X-Client-Id` request header, falling back to `"anonymous"`; this is the partition key, so each client gets its own primary and fallback pair. `storeHealth` is resolved from DI: the same singleton for every request, so the circuit breaker is shared across clients, as [The circuit breaker](01-concepts.md#the-circuit-breaker) explains it must be. `localPermitLimit` is `LocalBudget.ForReplicas` again, computed once at startup from `rateLimits.PermitLimit` and `rateLimits.TypicalReplicaCount` (34, the same number as [Step 3](#step-3-many-clients--partitions)). `ResilientRateLimiting.AspNetCore`'s `UseResilientDefaults` (set once, above this policy) makes a rejection a 429 response with the lease's `Retry-After` value written as the header, instead of the plain 503 that `AddRateLimiter` gives you by default.

**Run it.**

```bash
docker run -d --name rrl-getting-started-redis -p 6379:6379 redis:7
dotnet run --project samples/ResilientRateLimiting.Samples.Web --urls http://localhost:5299
```

**Try it with curl.** The limit is 100 requests per 60 seconds per client (`RateLimits` in `appsettings.json`). 105 requests from one client, Redis healthy:

```bash
for i in $(seq 1 105); do curl -s -o /dev/null -w "%{http_code}\n" -H "X-Client-Id: alice" http://localhost:5299/; done | sort | uniq -c
```

```text
    100 200
      5 429
```

A closer look at one allowed and one rejected response:

```text
$ curl -s -i -H "X-Client-Id: bob" http://localhost:5299/
HTTP/1.1 200 OK
Content-Type: text/plain; charset=utf-8
...
hello

$ curl -s -i -H "X-Client-Id: alice" http://localhost:5299/
HTTP/1.1 429 Too Many Requests
Content-Length: 0
...
```

Notice what is missing: no `Retry-After` header on that 429. This is not a bug in the sample. `RedisRateLimiting` 1.2.1's sliding window limiter gives no value under the standard `MetadataName.RetryAfter` name for a healthy rejection (**measured**, see [measurements.md](measurements.md#redisratelimiting-and-the-retry-after-value)), and this library only reads that standard name. [Retry-After](01-concepts.md#retry-after) explains why it has nothing honest to add here: the store is healthy, so this is not a degraded path, and the library will not invent a wait time for a limiter that gave none.

**Stop Redis and watch the fallback.** Stop the container (do not touch any container you did not start for this):

```bash
docker stop rrl-getting-started-redis
```

The very next request still goes through the normal path first and pays the store timeout (20 ms, plus the usual request overhead) before it gets an answer:

```text
$ time curl -s -o /dev/null -w "%{http_code}\n" -H "X-Client-Id: carol" http://localhost:5299/
200

real    0m0.104s
```

(the exact time varies by machine; the point is that it is close to the 20 ms timeout plus normal request overhead, not the several seconds a plain Redis client would wait)

The app's console shows why: the store call failed after the configured 20 ms, and (once enough calls have failed) the circuit breaker opens, exactly as [The circuit breaker](01-concepts.md#the-circuit-breaker) describes:

```text
warn: ResilientRateLimiting.StoreHealth[0]
      Store call failed: TimeoutRejectedException
      Polly.Timeout.TimeoutRejectedException: The store did not answer within 00:00:00.0200000.
Store failure: TimeoutRejectedException
...
warn: ResilientRateLimiting.StoreHealth[0]
      Store call failed: BrokenCircuitException
      Polly.CircuitBreaker.BrokenCircuitException: The circuit is now open and is not allowing calls.
Store failure: BrokenCircuitException
```

The two `Store failure:` lines are printed by this sample's own `OnStoreFailure` callback (see `Configure` near the top of `Program.cs`); the `warn:` lines above them are this library's own logging, wired up separately for the sample's second Redis connection with `StoreHealthOptions.WithLogging`. Once the breaker is open, requests stop waiting on the timeout at all and answer from the local fallback at once (requests dropped from tens of milliseconds to about a millisecond in this run). A new client, `erin`, gets the fallback's local budget of 34 admitted requests, then a rejection with both the degraded header and a real `Retry-After` this time — the local fallback limiter, unlike RedisRateLimiting, does give one:

```text
$ for i in $(seq 1 40); do curl -s -o /dev/null -w "%{http_code}\n" -H "X-Client-Id: erin" http://localhost:5299/; done | sort | uniq -c
     34 200
      6 429

$ curl -s -i -H "X-Client-Id: erin" http://localhost:5299/
HTTP/1.1 429 Too Many Requests
Retry-After: 118
X-RateLimit-Degraded: true
```

`X-RateLimit-Degraded: true` comes from `UseResilientDefaults(emitDegradedHeader: ...)`: the sample only adds it for loopback callers (so a health check or an internal caller can see the service is running degraded, without exposing that detail to the public internet).

`Retry-After: 118` (seconds) is not this library's own estimate here: the fallback is a `FixedWindowRateLimiter`, and the built-in class does give a `MetadataName.RetryAfter` value on a rejection (the time until its window resets). This library's `RetryAfterCalculator` prefers that value over its own estimate (`FallbackRecoveryTime` or `BreakDuration`), and only falls back to the estimate when the answering limiter gives no value at all — as it does not for `RedisRateLimiting`'s limiters (see [Step 4](#step-4-an-aspnet-core-app-with-redis)'s curl output above), for `FailClosed`, and for a `Recovery` refusal. On a degraded path the library also adds a random spread and, while degraded, a longer wait, both capped so their sum never exceeds `MaxAddedRetryDelay` (60 seconds by default). Because `erin`'s burst landed right after the fallback's one-minute window had just opened, the fixed window's own value here is close to a full 60 seconds; working through the same arithmetic for this configuration gives a range of about 108 to 120 seconds, which is where the console's 118 falls. Run the curl again yourself and you will likely see a different number in that range — the spread is random on purpose, so that many rejected clients do not all retry at the same moment. [Retry-After](01-concepts.md#retry-after) explains the three steps in general.

Stop the app (Ctrl+C) and the container (`docker stop rrl-getting-started-redis`, then `docker rm` it if you are done with it) when you are finished.

## Step 5: watch it

Every lease this library hands out is also counted in a metric, so you do not have to read logs to know how often the fallback answered instead of Redis. The web sample exports it with OpenTelemetry:

<!-- snippet: web-meter -->
```csharp
builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter(ResilientRateLimiter.MeterName));
```
From `samples/ResilientRateLimiting.Samples.Web/Program.cs`

`ResilientRateLimiter.MeterName` is the one constant name every metric from this library uses; `AddMeter` tells OpenTelemetry to collect it. [05-telemetry.md](05-telemetry.md) lists every instrument this meter reports (leases and store failures, each tagged with the policy and, for leases, the source) and how to turn them into alerts.

## What to read next

- [01-concepts.md](01-concepts.md) — the ideas behind this walk-through, if you skipped it.
- [03-api-reference.md](03-api-reference.md) — every public type and member, including the ones this page did not use.
- [04-configuration.md](04-configuration.md) — every option on `StoreHealthOptions` and `ResilientRateLimiterOptions`: what it means, its default, and when to change it.
- [05-telemetry.md](05-telemetry.md) — the metrics from Step 5, and useful alerts to build on them.

Next: [03-api-reference.md](03-api-reference.md).
