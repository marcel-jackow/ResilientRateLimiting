# ResilientRateLimiting

Resilience for distributed rate limiting: a timeout, a circuit breaker, and a local fallback for any store-backed `RateLimiter`, so a slow or down shared store never becomes an outage.

## What it is

`ResilientRateLimiting` is a decorator (a wrapper that adds behaviour around an object without changing how you call it) over any [`RateLimiter`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.ratelimiting.ratelimiter) whose count lives in a shared store, such as Redis. It counts nothing itself. The counting comes from a store-backed limiter, for example the `RedisRateLimiting` NuGet package, which this project tests against. What this library adds is what happens when that store is slow or unreachable: a short timeout, a circuit breaker, and a choice of what to do while the store cannot answer.

## The problem in 30 seconds

Many replicas of your service share one rate limit, kept as one counter in Redis. Redis slows down, or goes away for a minute. Without this library, every request now waits for the store's own timeout (often several seconds) and then fails, or hangs. The rate limiter meant to protect your service has become a way to take it down.

With this library: a request that would wait on a slow store instead waits at most a short, configured time. Once failures build up, a circuit breaker stops calling the store for a while. During that time, you choose what happens: answer from a small local budget, allow every request through, or reject every request. Once the store recovers, the library detects that and switches back automatically.

## Is it for me?

This library is for **overload protection**: limits measured in seconds to minutes, shared across replicas through a store such as Redis. It is not for daily or monthly quotas, or anything billing depends on — those must survive restarts and need an audit trail, so keep them in your business database instead. See [Which limiter should I use?](https://github.com/marcel-jackow/ResilientRateLimiting/blob/main/documentation/01-concepts.md#which-limiter-should-i-use) for the full comparison.

| Option | Pick it when |
|---|---|
| Built-in .NET limiters (no extra package) | One instance, or a per-replica limit is good enough |
| `RedisRateLimiting` alone | Many replicas share a limit and you accept what a slow or down Redis does to every request |
| `ResilientRateLimiting` (this library) + `RedisRateLimiting` | Many replicas share a limit and a Redis problem must not become an outage or a flood |
| `ResilientRateLimiting.AspNetCore` on top | The limiter protects an ASP.NET Core app |
| None of these | Daily/monthly quotas, billing — use your business database |

## Install

The quick start below needs two packages: `ResilientRateLimiting` (the resilience wrapper) and `RedisRateLimiting` (a separate, open-source package that does the actual counting in Redis). Add `ResilientRateLimiting.AspNetCore` too if you build an ASP.NET Core app; see [ASP.NET Core in brief](#aspnet-core-in-brief).

```bash
dotnet add package ResilientRateLimiting
dotnet add package RedisRateLimiting
dotnet add package ResilientRateLimiting.AspNetCore
```

These packages are not on nuget.org yet (this library is at version 0.1.0, unpublished). Until the first release, reference the projects directly from a clone of this repository instead:

```bash
dotnet add reference path/to/ResilientRateLimiting/src/ResilientRateLimiting/ResilientRateLimiting.csproj
dotnet add reference path/to/ResilientRateLimiting/src/ResilientRateLimiting.AspNetCore/ResilientRateLimiting.AspNetCore.csproj
```

## Quick start

One rate limiter backed by Redis, with resilience added:

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

`primary` is the limiter that does the real counting, in Redis. `fallback` answers while Redis cannot; `LocalBudget.ForReplicas` divides the shared limit by the typical replica count. `WithResilience` wraps `primary` with the timeout, the circuit breaker, and `fallback` as the outage behaviour. See [02-getting-started.md](https://github.com/marcel-jackow/ResilientRateLimiting/blob/main/documentation/02-getting-started.md) for a line-by-line walk of this same example, from an empty project.

## ASP.NET Core in brief

`ResilientRateLimiting.AspNetCore` wires this library into ASP.NET Core's own rate-limiting middleware. `AddResilientRateLimiting` registers the shared `StoreHealth` from configuration:

<!-- snippet: web-services -->
```csharp
builder.Services.AddSingleton(redis);
builder.Services.AddResilientRateLimiting(
    builder.Configuration.GetSection("ResilientRateLimiting:Store"),
    Configure);
```
From `samples/ResilientRateLimiting.Samples.Web/Program.cs`

A policy supplies the partition and uses `ResilientRateLimitPartition` in place of the built-in partition helpers:

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

`UseResilientDefaults` (set once, alongside the policy) turns a rejection into a 429 response with the `Retry-After` header written from the lease, instead of the plain 503 `AddRateLimiter` gives by default. See [Step 4 of 02-getting-started.md](https://github.com/marcel-jackow/ResilientRateLimiting/blob/main/documentation/02-getting-started.md#step-4-an-aspnet-core-app-with-redis) for the full sample, including configuration and the degraded header.

## Documentation

- [01-concepts.md](https://github.com/marcel-jackow/ResilientRateLimiting/blob/main/documentation/01-concepts.md) — what a rate limiter does, the kinds of limiter, which one to use, the circuit breaker, the local fallback, and the glossary.
- [02-getting-started.md](https://github.com/marcel-jackow/ResilientRateLimiting/blob/main/documentation/02-getting-started.md) — a walk from an empty console app to an ASP.NET Core app with Redis, five runnable steps.
- [03-api-reference.md](https://github.com/marcel-jackow/ResilientRateLimiting/blob/main/documentation/03-api-reference.md) — every public type and member, with an example, common mistakes, and what it throws.
- [04-configuration.md](https://github.com/marcel-jackow/ResilientRateLimiting/blob/main/documentation/04-configuration.md) — every option: what it means, why its default is what it is, when to change it.
- [05-telemetry.md](https://github.com/marcel-jackow/ResilientRateLimiting/blob/main/documentation/05-telemetry.md) — the metrics, the store failure reports, and how your own code can tell which path answered a request.
- [06-production.md](https://github.com/marcel-jackow/ResilientRateLimiting/blob/main/documentation/06-production.md) — running against a real Redis: memory, timeouts, restarts during an outage, and known limits.
- [measurements.md](https://github.com/marcel-jackow/ResilientRateLimiting/blob/main/documentation/measurements.md) — the real numbers behind every default this documentation calls **measured**.

## Status

Version 0.1.0. The API may change before 1.0.

## License

[MIT](https://github.com/marcel-jackow/ResilientRateLimiting/blob/main/LICENSE)
