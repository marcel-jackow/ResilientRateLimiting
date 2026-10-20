# Samples

Two small programs that show the library in use, and a demo rig that runs the web sample three times behind a gateway. Every code example in the [documentation](../documentation/) is copied from these files, and a test checks that the copies stay exact, so what you read in the docs is code that builds.

| Project | What it shows | Needs Redis? |
|---|---|---|
| [`ResilientRateLimiting.Samples.Console`](ResilientRateLimiting.Samples.Console/) | Every part of the core package, one short scenario each | Only for the first scenario |
| [`ResilientRateLimiting.Samples.Web`](ResilientRateLimiting.Samples.Web/) | A minimal ASP.NET Core app with the `ResilientRateLimiting.AspNetCore` package and real Redis | Yes |
| [`ResilientRateLimiting.Samples.AppHost`](ResilientRateLimiting.Samples.AppHost/) | The [Aspire demo rig](#the-aspire-demo-rig): Redis, three copies of the web sample and a gateway, driven from the Aspire dashboard | Starts its own, in Docker |

You need the .NET SDK that the repository's `global.json` names, or a newer one of the same major version. For Redis, Docker is the easiest way:

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

- `GET /` uses the policy `per-client`, on the Redis connection registered through `AddResilientRateLimiting`. With the setting `RateLimits:Mode` set to `InMemory` (the default is `Resilient`), it uses `per-client-in-memory` instead: a plain ASP.NET Core limiter that counts only in this process. The Aspire demo rig uses it to show the problem the library solves.
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

## The Aspire demo rig

`samples/ResilientRateLimiting.Samples.AppHost` runs the whole story on one machine: Redis, three copies of the web sample (`api-a`, `api-b`, `api-c`) and a gateway in front of them ([`ResilientRateLimiting.Samples.Gateway`](ResilientRateLimiting.Samples.Gateway/)). You need Docker and the [Aspire CLI](https://aspire.dev/get-started/install-cli/). The CLI version must match the Aspire packages the AppHost uses (13.5 or later); an older CLI starts the app but then cannot find it, and `aspire start` ends with "Timeout waiting for AppHost to start". `aspire update --self` updates the CLI.

```bash
cd samples/ResilientRateLimiting.Samples.AppHost
aspire run
```

Open the dashboard link that `aspire run` prints. The demo uses a small limit so each effect shows within seconds: 30 requests per 10 seconds per client, shared by the three APIs. During a Redis outage each API falls back to a local budget of 10. The rig also sets the store timeout to 200 ms instead of the sample's 20 ms: right after a start, the first Redis calls are slower, and with 20 ms some of them would count as store failures and change the numbers. Keep the timeout short in production.

The gateway resource has the commands for the whole demo (the "…" menu in the dashboard, then Commands, or `aspire resource gateway <name>` in a second terminal). The numbered ones are the demo script, scene by scene, in the order you click them:

| Command | Name | What it does |
|---|---|---|
| 1.1 Send burst of 90 | `burst-1` | 90 requests as a new client, one every 10 ms (about one second), so the three APIs take turns; the result says how many were allowed, limited and failed. 2.2 and 3.3 do the same |
| 2.1 Switch to shared Redis | `limiter-redis` | restarts the three APIs so all count in one shared Redis through the library, and waits until they answer again |
| 2.2 Send burst of 90 | `burst-2` | as 1.1 |
| 3.1 Start steady load | `start-load` | 5 requests per second as client `steady`: more than the limit, so the charts show both allowed and limited |
| 3.2 Stop Redis | `redis-stop` | the same as Stop on the `redis` resource |
| 3.3 Send burst of 90 | `burst-3` | as 1.1 |
| 4.1 Start Redis | `redis-start` | the same as Start on the `redis` resource |
| 4.2 Stop steady load | `stop-load` | stops the steady load |
| Limiter: in-memory | `limiter-memory` | restarts the three APIs so each counts on its own in memory, for a second run of the demo |
| Traffic: even / Traffic: 60/30/10 | `traffic-even` / `traffic-uneven` | restarts the gateway with the chosen shares (60/30/10 sends 60 % to `api-a`, 30 % to `api-b`, 10 % to `api-c`) |
| Budget: equal share / Budget: as if 2 replicas | `budget-equal` / `budget-2` | restarts the three APIs with a local budget of 10 or 15, and waits until they answer again |

The commands that restart the APIs need Redis running: an API cannot start without Redis. Each command writes its result to the gateway's console log (Console tab in the dashboard, or `aspire logs gateway`). The CLI prints only whether a command worked, plus the reason when it did not.

The rig starts with the in-memory limiter and even traffic, so the demo can begin with the problem.

**A demo in five scenes.** Watch the Metrics tab: `resilient_rate_limiting.leases`, split by the `source` and `outcome` tags. Restart the AppHost right before you present: the APIs report running totals, so older bursts would still show.

1. **In memory on three replicas.** 1.1: all 90 allowed, 30 on each API, because each API counts only its own requests. Three replicas let through three times the limit. In the Metrics tab, `aspnetcore.rate_limiting.requests` split by `aspnetcore.rate_limiting.result` shows it per API. Run this scene with even traffic (the start setting); with 60/30/10 it gives 30 + 27 + 9 = 66.
2. **Shared limit.** 2.1, then 2.2: 30 allowed, 60 limited, and in the Metrics tab each API shows 10 allowed and 20 limited. That is one limit for all three APIs, because they count in one Redis. If a store call is ever slower than the store timeout, the number can be off by a few: that request is answered from its API's local budget instead of the shared counter.
3. **Redis outage.** 3.1 and 3.2. The `source` changes from `distributed` to `local_fallback`: requests are still answered, and still limited. Wait about 10 seconds, then 3.3: 30 get through, 10 from each API's local budget.
4. **Recovery.** 4.1. The `source` shows `recovery` for one window (10 seconds), then `distributed`. Then 4.2.
5. **Uneven traffic** (optional). Traffic: 60/30/10, then 3.2 Stop Redis. A burst now sends 54 requests to `api-a`, 27 to `api-b` and 9 to `api-c`. `api-a` and `api-b` stop at their local budget of 10, and `api-c` gets only 9 requests, so about 29 get through: fewer than the shared limit allows. That is safe, but conservative, and the more uneven the traffic, the bigger the gap. Start Redis (4.1), use Budget: as if 2 replicas, and stop Redis again: now about 39 get through (15 + 15 + 9), more than the shared limit of 30. The numbers in this scene can differ by one when the steady load runs, because it goes through the same gateway during the burst and shifts the 60/30/10 order a little. That is the trade-off of sizing the local budget.
