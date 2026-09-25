# Samples

Two small programs that show the library in use. Every code example in the [documentation](../documentation/) is copied from these files, and a test checks that the copies stay exact, so what you read in the docs is code that builds.

| Project | What it shows | Needs Redis? |
|---|---|---|
| [`ResilientRateLimiting.Samples.Console`](ResilientRateLimiting.Samples.Console/) | Every part of the core package, one short scenario each | Only for the first scenario |
| [`ResilientRateLimiting.Samples.Web`](ResilientRateLimiting.Samples.Web/) | A minimal ASP.NET Core app with the `ResilientRateLimiting.AspNetCore` package and real Redis | Yes |

You need the .NET 10 SDK. For Redis, Docker is the easiest way:

```bash
docker run -d --name rrl-samples-redis -p 6379:6379 redis:7
```

Stop it again with `docker stop rrl-samples-redis`.

## Console sample

```bash
dotnet run --project samples/ResilientRateLimiting.Samples.Console
dotnet run --project samples/ResilientRateLimiting.Samples.Console -- --redis
```

Without `--redis`, the first scenario is skipped and everything else runs with no Redis at all. To make that possible, most scenarios use two stand-in classes from [`StoreStandIns.cs`](ResilientRateLimiting.Samples.Console/StoreStandIns.cs) in place of a Redis-backed limiter:

- `InMemoryStore` counts in memory and behaves like a healthy shared store.
- `UnreachableStore` always fails, like a store that cannot be reached. It lets you see the fallback paths without waiting for a real timeout.

The program runs the scenarios below in this order and prints a heading (`== name ==`) and the result of each. The code of each scenario is a method in [`Scenarios.cs`](ResilientRateLimiting.Samples.Console/Scenarios.cs), marked with `// snippet: name`. The last column links to the page that explains it.

| Scenario | What it shows | Explained in |
|---|---|---|
| `quick-start` | The smallest complete setup with real Redis (runs only with `--redis`) | [Getting started, Step 1](../documentation/02-getting-started.md#step-1-a-limiter-in-a-console-app-no-redis) |
| `built-in-limiter` | A plain .NET limiter, before this library is involved | [What a rate limiter does](../documentation/01-concepts.md#what-a-rate-limiter-does) |
| `limiter-kinds` | Fixed window, sliding window, token bucket and concurrency limiters side by side | [Kinds of limiter](../documentation/01-concepts.md#kinds-of-limiter) |
| `built-in-partitioned` | One limiter per key with plain .NET | [Partitions](../documentation/01-concepts.md#partitions) |
| `store-health` | One `StoreHealth` per store connection, with explicit options | [API reference: `new StoreHealth`](../documentation/03-api-reference.md#storehealth-constructor) |
| `local-budget` | Sizing the fallback with `LocalBudget.ForReplicas` | [The local budget](../documentation/01-concepts.md#the-local-budget) |
| `constructor` | Building a `ResilientRateLimiter` with its constructor | [API reference: constructor](../documentation/03-api-reference.md#constructor) |
| `with-resilience` | The same with the `WithResilience` extension method | [API reference: `WithResilience`](../documentation/03-api-reference.md#withresilience) |
| `partitioned` | One resilient limiter per key with `ResilientRateLimitPartition.Get` | [Getting started, Step 3](../documentation/02-getting-started.md#step-3-many-clients--partitions) |
| `partition-metrics-tag` | Tagging metrics with the partition key | [API reference: `ResilientRateLimitPartition.Get`](../documentation/03-api-reference.md#partition-get) |
| `fail-open` | No fallback limiter: admit requests while the store fails | [Getting started, Step 2](../documentation/02-getting-started.md#step-2-see-the-fallback-work) |
| `fail-closed` | No fallback limiter: reject requests while the store fails | [Getting started, Step 2](../documentation/02-getting-started.md#step-2-see-the-fallback-work) |
| `read-source` | Finding out which path answered a request | [Reading the source in your own code](../documentation/05-telemetry.md#reading-the-source-in-your-own-code) |
| `read-retry-after` | Reading the retry time from a rejected lease | [Retry-After](../documentation/01-concepts.md#retry-after) |
| `store-failure-callback` | `ShouldHandle` and `OnStoreFailure` on the store options | [Store failure reports](../documentation/05-telemetry.md#store-failure-reports) |
| `options-copy` | Options are records: copy with `with`, never change in place | [API reference: `ResilientRateLimiterOptions`](../documentation/03-api-reference.md#limiter-options) |
| `validate` | `Validate()` reports every mistake in one exception | [API reference: `Validate()`](../documentation/03-api-reference.md#limiter-options-validate) |
| `time-provider` | Passing a `TimeProvider` | [API reference: `WithResilience`](../documentation/03-api-reference.md#withresilience) |
| `acquire-async-not-attempt` | Why you call `AcquireAsync`: `AttemptAcquire` always rejects | [API reference: `AttemptAcquire`](../documentation/03-api-reference.md#attemptacquire) |
| `dispose` | Dispose only the wrapper; it disposes both limiters | [API reference: `Dispose`](../documentation/03-api-reference.md#dispose) |
| `permit-count-check` | Checking a permit count against the store's limit before asking | [API reference: `AcquireAsync`](../documentation/03-api-reference.md#acquireasync) |
| `token-bucket-fallback` | A token bucket fallback and how to compute its `FallbackRecoveryTime` | [API reference: `FallbackRecoveryTime`](../documentation/03-api-reference.md#fallbackrecoverytime) |
| `sliding-window-fallback` | A sliding window fallback: the recovery time is the whole window | [API reference: `FallbackRecoveryTime`](../documentation/03-api-reference.md#fallbackrecoverytime) |
| `partition-no-fallback` | Partitions with no fallback factory, here with `FailClosed` | [API reference: `ResilientRateLimitPartition.Get`](../documentation/03-api-reference.md#partition-get) |
| `store-health-time-provider` | A `StoreHealth` with its own `TimeProvider` | [API reference: `new StoreHealth`](../documentation/03-api-reference.md#storehealth-constructor) |
| `store-health-validate` | `StoreHealthOptions.Validate()` | [API reference: `StoreHealthOptions.Validate()`](../documentation/03-api-reference.md#storehealthoptions-validate) |
| `limiter-options-all` | Every limiter option set in one place | [API reference: `ResilientRateLimiterOptions`](../documentation/03-api-reference.md#limiter-options) |
| `dispose-async` | Disposing asynchronously | [API reference: `Dispose`](../documentation/03-api-reference.md#dispose) |
| `custom-lease` | Building a `ResilientRateLimitLease` yourself, for tests or your own decorator | [API reference: lease constructor](../documentation/03-api-reference.md#lease-constructor) |

## Web sample

```bash
dotnet run --project samples/ResilientRateLimiting.Samples.Web --urls http://localhost:5299
curl -i -H "X-Client-Id: alice" http://localhost:5299/
```

It is one ASP.NET Core app with two endpoints, each limited per client:

- `GET /` uses the policy `per-client`, on the Redis connection registered through `AddResilientRateLimiting`.
- `GET /secondary` uses the policy `per-client-secondary`, on a second Redis connection with its own hand-built `StoreHealth`. It shows how to add a second store connection without registering a second `StoreHealth` in dependency injection.

Settings come from [`appsettings.json`](ResilientRateLimiting.Samples.Web/appsettings.json): the Redis connection strings, the store options (set to the library's defaults) and the example limit of 100 requests per 60 seconds per client. Stop Redis while the app runs to watch the local fallback answer instead.

> **Not a production setup.** The sample takes the client's partition key from the `X-Client-Id` request header, only to keep it short and easy to try with `curl`. The caller controls every header, so a caller could send a new value on each request to get a fresh budget every time, or send another client's value to use up that client's budget. In production, take the key from data the server trusts, for example a claim of the signed-in user.

The code is in [`Program.cs`](ResilientRateLimiting.Samples.Web/Program.cs), in these marked regions:

| Region | What it shows | Explained in |
|---|---|---|
| `web-async-timeout` | The Redis client's own timeout, set at or below `StoreTimeout` | [Timeouts](../documentation/06-production.md#timeouts) |
| `web-configure` | Setting `ShouldHandle` and `OnStoreFailure`, which configuration cannot bind | [`ShouldHandle` and `OnStoreFailure`](../documentation/04-configuration.md#shouldhandle-and-onstorefailure) |
| `web-services` | Registering the store connection with `AddResilientRateLimiting` | [Getting started, Step 4](../documentation/02-getting-started.md#step-4-an-aspnet-core-app-with-redis) |
| `web-with-logging` | A second store connection with logging attached by hand | [API reference: `WithLogging`](../documentation/03-api-reference.md#withlogging) |
| `web-degraded-header` | `UseResilientDefaults`: 429, `Retry-After` and the degraded header | [The degraded header](../documentation/05-telemetry.md#the-degraded-header) |
| `web-policy` | A rate-limiting policy built with `ResilientRateLimitPartition.Get` | [Getting started, Step 4](../documentation/02-getting-started.md#step-4-an-aspnet-core-app-with-redis) |
| `web-meter` | Collecting the library's metrics with OpenTelemetry | [Getting started, Step 5](../documentation/02-getting-started.md#step-5-watch-it) |

[Getting started](../documentation/02-getting-started.md) walks through both samples step by step, with real output.
