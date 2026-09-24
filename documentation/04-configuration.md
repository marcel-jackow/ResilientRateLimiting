# Configuration

This page is about choosing values, not about types and signatures — [03-api-reference.md](03-api-reference.md) already lists every property's type, default and valid range. Here, for each option: what it means, why its default is what it is, when to change it, and what goes wrong if you set it too low or too high.

Every default on this page is labelled **measured** (with a link to [measurements.md](measurements.md)) or **starting point**. A starting point is a reasonable value to launch with, not a number proven right for your traffic and your network. Only `StoreTimeout` and `MaxWarmPartitions` have a measured number behind them; every other default is a starting point.

If a word is new, its full explanation is in the [glossary](01-concepts.md#glossary) in [01-concepts.md](01-concepts.md).

## Two kinds of settings

The library has two options types, and they do not work the same way.

| | `StoreHealthOptions` | `ResilientRateLimiterOptions` |
|---|---|---|
| Shared by | every limiter that uses one [store connection](01-concepts.md#store-connection) | one limiter, or every partition of one policy |
| Held by | [`StoreHealth`](03-api-reference.md#storehealth) | passed straight to the limiter |
| Can come from `appsettings.json`? | yes, through [`AddResilientRateLimiting`](03-api-reference.md#addresilientratelimiting) | no — always built in code |
| Built | once per store connection | once per policy |

**`StoreHealthOptions` is store-scoped.** One [`StoreHealth`](01-concepts.md#storehealth) holds the [circuit breaker](01-concepts.md#circuit-breaker) and the count of live partitions for one connection — for example, one Redis server. In an ASP.NET Core app, `AddResilientRateLimiting` binds this type from configuration for you, which is why it is the one type that can live in `appsettings.json`.

**`ResilientRateLimiterOptions` is limiter-scoped**, and the library gives it no configuration-binding path at all. Build it once in code, at startup, and hand the same instance to every partition of a policy — the sample does this: `policyOptions` is built once, above `AddRateLimiter`, and the per-request policy lambda only reads it (`samples/ResilientRateLimiting.Samples.Web/Program.cs`, the `policyOptions` variable used inside `AddPolicy("per-client", ...)`).

**The cost of building it inside the per-request policy lambda.** Nothing stops you from writing `new ResilientRateLimiterOptions { ... }` inside the lambda that `AddPolicy` calls on every request. It still works — [`ResilientRateLimitPartition.Get`](03-api-reference.md#partition-get) only actually builds a limiter, and calls [`Validate()`](03-api-reference.md#limiter-options-validate), the first time it sees a new [partition key](01-concepts.md#partition-key). But there is no `ValidateOnStart` for this type (only the configuration-bound `StoreHealthOptions` gets that). So a mistake — a missing `FallbackRecoveryTime`, for example — does not stop the app at startup. It waits, silently, until the first real request for the first real partition key hits it, and only then throws. Building the options once, before `AddRateLimiter` runs, does not fix this by itself (the check still only runs on first use per key), but it means you have one object you can call `Validate()` on by hand at startup, instead of an object that is rebuilt fresh, unchecked, on every request.

## `StoreHealthOptions` — store connection settings

### `StoreTimeout`

**Meaning.** How long to wait for the store before the call counts as a [store failure](01-concepts.md#store-failure) and the outage path answers instead.

**Default.** 20 ms — a **starting point**, not a value measured for your deployment. [measurements.md#store-round-trip](measurements.md#store-round-trip) reports the **measured** round trip against a local Redis (p50 0.42 ms, p99 0.87 ms) and its own advice: measure your own p99 round trip in production and set `StoreTimeout` to about five times that.

**When to change it.** As soon as you have a real measurement from your own network and Redis tier. A store reached across a cloud region typically adds several milliseconds over a local one; across regions, tens of milliseconds. The 20 ms default assumes a Redis close to the process.

**Too low.** Ordinary latency spikes get treated as store failures. Each one throws away a real answer, charges the [fallback limiter](01-concepts.md#fallback-limiter) instead, and counts toward opening the [circuit breaker](01-concepts.md#circuit-breaker) — so a store that is merely a little slow can look like a store that is down.

**Too high.** A genuine outage takes longer to notice. Every request during that time waits close to the full timeout before the fallback path answers, which is the one thing `StoreTimeout` exists to bound.

**One more thing to set alongside it.** The Redis client has its own timeout (for example `AsyncTimeout` on `StackExchange.Redis`). Set it at or below `StoreTimeout`. If the client's own timeout is longer, this library stops waiting and moves on at `StoreTimeout`, but the abandoned call keeps running inside the client until the client's own, longer timeout — so the store connection stays busy with work nobody is waiting for any more. [06-production.md](06-production.md) covers this in more depth.

**Valid range.** Greater than zero.

### `FailuresBeforeOpen` and `FailureRatio`

**Meaning together.** These two settings decide when the [circuit breaker](01-concepts.md#circuit-breaker) opens. Within one [breaker sampling duration](01-concepts.md#breaker-sampling-duration) window, the breaker may open only once **both** are true: at least `FailuresBeforeOpen` store calls happened, **and** the share of those calls that failed is at least `FailureRatio`. "At least" on the ratio matters: a failure share exactly equal to `FailureRatio` is enough to open the breaker, not one call more.

**Defaults.** `FailuresBeforeOpen` is 5, `FailureRatio` is 0.5 — both **starting points**.

**Worked example, at the defaults.** Say 6 store calls happen inside one 10-second `BreakerSamplingDuration` window (the default).

- If 3 of those 6 calls fail, the failed share is 3 ÷ 6 = 0.5 — exactly `FailureRatio`. That is enough calls (6 ≥ 5) and enough failures, so the breaker opens.
- If only 2 of the 6 fail, the share is about 0.33, below 0.5. Enough calls happened, but not enough of them failed, so the breaker stays closed.
- If only 4 calls happen in the window and all 4 fail, the failed share is 1.0 — every call failed — but only 4 calls happened, below the 5 required. The breaker still stays closed: it will not open on too few calls, no matter how bad they look.

**When to change them.** Raise `FailuresBeforeOpen` for a low-traffic policy where a handful of calls is not enough evidence that the store, rather than one unlucky client, is the problem. Lower `FailureRatio` if you want the breaker to react to a smaller share of failures (for a resource where a partial outage is already too costly to keep sending more calls at).

**Too low (either one).** The breaker opens on ordinary noise — a couple of slow calls at the wrong moment — and treats a healthy store as failed. Every partition on that connection then answers from the fallback path (or fails open or closed) for the whole `BreakDuration`, for no real reason.

**Too high (either one).** The breaker takes longer to notice a real outage, or needs an unrealistically bad failure share first. Every request in the meantime pays the full `StoreTimeout` before falling back, one at a time, instead of the breaker short-circuiting them.

**Valid range.** `FailuresBeforeOpen`: at least 2 (the breaker library underneath cannot open on a single failure). `FailureRatio`: greater than 0, at most 1 (a value of 1 means every sampled call must fail).

### `BreakDuration`

**Meaning.** How long the breaker stays open — no calls reach the store at all — before it lets one call through to test whether the store has recovered.

**Default.** 5 seconds — a **starting point**.

**When to change it.** Shorter if you want to notice recovery sooner and can accept more test calls against a store that might still be down. Longer if a still-failing store is costly to keep probing (for example, it adds load to a system that is already struggling).

**Too short.** The breaker tests the store so often that a genuine, longer outage barely differs from the breaker being half-open the whole time: repeated failed test calls, repeated fallback answers, repeated `OnStoreFailure` reports.

**Too long.** The service keeps answering from the fallback path (or fails open or closed) for longer than the real outage lasted, because the breaker will not even try the store again until the duration elapses.

**Valid range.** Greater than zero.

### `BreakerSamplingDuration`

**Meaning.** The rolling time window in which the breaker counts calls and failures for `FailuresBeforeOpen` and `FailureRatio`. It also sets how long an exception type must stay quiet before `OnStoreFailure` reports that type again.

**Default.** 10 seconds — a **starting point**.

**When to change it.** Shorter reacts to a change in the store's health faster, but with fewer calls in the window, a handful of failures swings the ratio a lot. Longer smooths that out, but reacts more slowly and re-reports a repeating failure less often.

**Too short.** A tiny burst of bad luck — two failures out of three calls — can look like a serious failure ratio simply because so few calls fit in the window.

**Too long.** The breaker (and `OnStoreFailure`) is slow to notice that the store's condition has changed, in either direction: slow to open once real trouble starts, and slow to consider a repeating problem "new" again after it has actually stopped.

**Valid range.** At least 500 ms; the breaker library underneath refuses a shorter window.

### `MaxWarmPartitions`

**Meaning.** Above this many live partitions on one store connection, [warm fallback state](01-concepts.md#warm-fallback-state) is released early for **every** partition on that connection, not only the extra ones. This bounds total memory instead of bounding it per key.

**Default.** 10,000 — **measured**. [measurements.md#memory-per-partition](measurements.md#memory-per-partition) reports about 1 KB per warm partition (1,027 bytes with a Redis primary, 936 with an in-memory primary, both with a fixed-window fallback). At 10,000 partitions that is about 10 MB, small enough that a service at or below this count never has to think about the number.

**When to change it.** Raise it if your service legitimately has more than 10,000 concurrently active partition keys and can spare a few more megabytes to keep them all warm. Lower it if memory is tight and you would rather release warm state earlier, at the cost of weaker protection right after an outage for the partitions that lose it.

**Too low.** Warm state is released constantly, even under ordinary load, so more clients start an outage from an empty local counter — losing the protection [warm fallback state](01-concepts.md#warm-fallback-state) exists to give.

**Too high.** Memory keeps growing with the number of active partitions, roughly 1 KB each (measured, fixed-window fallback only — a sliding-window or token-bucket fallback was not measured and costs more per partition), until something else has to bound it.

**Valid range.** At least 1.

### `ColdStartFallbackFactor`

**Meaning.** Until some limiter on this store connection has reached the store at least once, each request answered by the [local fallback](01-concepts.md#local-fallback) is charged more than its real permit count: the permit count divided by this factor, rounded up. It only charges extra — it never resizes the fallback limiter itself. A factor of 1.0 charges exactly the real permit count, which is the same as no effect at all.

**Default.** 1.0 (no effect) — a **starting point**.

**When to change it.** Lower it where restarts and store outages tend to happen together — for example, the same network problem that makes Redis unreachable often also triggers a wave of pod restarts. A newly started replica's fallback limiter is empty, but that does not mean the client's share of the limit is unused: another replica may have just spent it. A factor below 1.0 makes a fresh replica serve a smaller slice of its local budget until it proves the store is reachable.

**Too low (close to zero).** Every restart — even a routine deployment, with the store perfectly healthy — temporarily charges far more per request than it should, because the rule applies from process start regardless of whether the store is actually down. Legitimate traffic gets rejected during a normal rollout.

**Too high (the default, 1.0, meaning no effect).** During an outage that coincides with restarts, each fresh replica allows its full local budget on top of what the same clients already spent through other, longer-lived replicas — the exact overshoot that cold start exists to catch (see [Cold start](01-concepts.md#cold-start)).

**Valid range.** Greater than 0, at most 1.

### `ShouldHandle` and `OnStoreFailure`

**Meaning.** `ShouldHandle` decides whether an exception thrown by the store limiter counts as a [store failure](01-concepts.md#store-failure) (the library's own timeout and open-breaker exceptions always count, regardless of this). `OnStoreFailure` is called when a store failure happens, so you can log or alert on it.

**Default.** Both `null` — a **starting point** meaning "use the library's own classification, and do not report failures anywhere."

**Why they cannot come from `appsettings.json`.** Both are delegates — a `Func<Exception, bool>` and an `Action<Exception>` — and JSON configuration binds only data (strings, numbers, booleans, nested objects), never executable code. There is no text representation of "call this method." Set them through the `configure` callback that `AddResilientRateLimiting` accepts, which runs after the rest of `StoreHealthOptions` is bound from configuration:

<!-- snippet: web-configure -->
```csharp
StoreHealthOptions Configure(StoreHealthOptions options) => options with
{
    // The library's own timeout and breaker always count as store failures, so this only names the store's own exceptions.
    ShouldHandle = exception => exception is RedisException or TimeoutException,
    OnStoreFailure = exception => Console.WriteLine($"Store failure: {exception.GetType().Name}"),
};
```
From `samples/ResilientRateLimiting.Samples.Web/Program.cs`

**When to change them.** Set `ShouldHandle` once you know which exceptions your store client throws for a real outage (for example `RedisException` for `StackExchange.Redis`), so that only those exceptions are treated as store failures. Include the client's own timeout exception too: `StackExchange.Redis`'s `RedisTimeoutException` derives from the .NET `TimeoutException`, not from `RedisException`, so a predicate that checks only for `RedisException` misses it and sends a client-side timeout straight to your caller instead of the fallback path. Set `OnStoreFailure` to feed your own logging or metrics beyond what [05-telemetry.md](05-telemetry.md) already covers.

**What goes wrong if wrong.** A `ShouldHandle` that returns `false` for exceptions it does not recognise sends those exceptions straight to your caller instead of the fallback path — a real store error can then become an unhandled exception in your app. A slow `OnStoreFailure` (for example, a blocking network call) delays the very request that just hit a store failure, because it runs on the request path.

**Valid range.** Any predicate or callback, or `null` for either.

## `ResilientRateLimiterOptions` — per-limiter settings

### `FailureBehavior`

**Meaning.** Which path answers when the store fails: `LocalFallback`, `FailOpen`, or `FailClosed`. See [`StoreFailureBehavior`](03-api-reference.md#storefailurebehavior) for the full comparison, and [The fallback limiter](#the-fallback-limiter) below for what `LocalFallback` needs.

**Default.** `LocalFallback` — a **starting point**: the safe middle ground between "no protection during an outage" (`FailOpen`) and "no service during an outage" (`FailClosed`).

**When to change it.** Pick `FailOpen` where the limit is a courtesy and refusing a real customer is worse than letting extra traffic through for a while. Pick `FailClosed` where the limit protects something that must never be overrun (a paid quota, a fragile downstream system) and refusing is safer than overshooting.

**What goes wrong if wrong.** `LocalFallback` needs a fallback limiter and `FallbackRecoveryTime`; leaving those out fails validation. `FailOpen` during an outage removes all protection — a client that misbehaves at exactly that moment reaches your service unlimited. `FailClosed` during an outage turns a store problem into an outage of your own endpoint.

**Valid range.** `LocalFallback`, `FailOpen`, `FailClosed`.

### `FallbackRecoveryTime`

**Meaning.** How long your fallback limiter needs to go from fully used back to fully available, with no traffic. It drives three things:

- **Recovery mode.** After the store answers again following an outage, the limiter stays in [recovery mode](01-concepts.md#recovery-mode) for this long, checking the local counter first (the recovery gate) before it trusts the store's own count again. This stops a client getting a second allowance right after the outage ends.
- **Warm retention.** An idle partition keeps [warm fallback state](01-concepts.md#warm-fallback-state) for this long, or for `MaxWarmRetention`, whichever is shorter.
- **The degraded Retry-After estimate.** When a degraded path answers with no Retry-After value of its own, the library estimates one from this value (or, if it is zero, from `BreakDuration`).

**Default.** No default that works — it starts at zero, and zero is rejected whenever `FailureBehavior` is `LocalFallback`. There is no starting point that fits every fallback limiter, which is exactly why the library asks you to set it explicitly rather than guessing.

**Why the library cannot read it from the limiter itself, and must ask you instead.** It would be simpler if the library just asked the fallback limiter how long it takes to refill. It cannot, because the base `RateLimiter` class in `System.Threading.RateLimiting` does not expose that:

| Fallback limiter | Public information | Can the library compute recovery time? |
|---|---|---|
| `FixedWindowRateLimiter` (1 min) | `ReplenishmentPeriod` = 1 min | yes — the period equals the window |
| `SlidingWindowRateLimiter` (1 min, 6 segments) | `ReplenishmentPeriod` = 10 s | no — that is one [segment](01-concepts.md#segment); the number of segments is not public |
| `TokenBucketRateLimiter` (100 tokens, 10/s) | `ReplenishmentPeriod` = 1 s | no — `TokenLimit` and `TokensPerPeriod` are not public |
| custom or wrapped limiter | `IdleDuration` only | no |

`GetStatistics()` does not help either — it reports live counters (permits available, queue length), not configuration. The library could compute the value only for a fixed window, and only by coincidence. One rule that always applies — "required with `LocalFallback`, for every kind of limiter" — is easier to learn and harder to get wrong by accident than "optional for one kind, required for the other three."

**The value to choose, by kind of fallback limiter.**

| Fallback limiter | `FallbackRecoveryTime` |
|---|---|
| Fixed window | the window |
| Sliding window | the whole window, not one segment |
| Token bucket | `TokenLimit ÷ TokensPerPeriod`, rounded up, × `ReplenishmentPeriod` |
| Custom limiter | the time from fully used to fully available, with no traffic |
| Concurrency limiter | not allowed as a fallback — see [The fallback limiter](#the-fallback-limiter) |

**Too large** (for example, minutes typed where seconds were meant). Every degraded Retry-After estimate tells clients to wait for that whole time, and after each outage the limiter stays in the stricter, per-replica recovery mode for just as long.

**Too small** (for example, one sliding-window segment instead of the whole window). Recovery mode ends before the fallback limiter has genuinely refilled, so a client can get part of a second allowance right after an outage — the exact problem recovery mode exists to prevent — and warm state is released earlier than it should be.

**The one-day bound.** [`Validate()`](03-api-reference.md#limiter-options-validate) rejects any value above [`MaxFallbackRecoveryTime`](03-api-reference.md#maxfallbackrecoverytime) (one day), for **every** `FailureBehavior`, not only `LocalFallback` — because the degraded Retry-After estimate reads this value no matter which behaviour is chosen. A value that large almost always means a unit mistake (hours or days typed where minutes were meant), and would otherwise tell clients to wait for days. See [Choosing a window length](01-concepts.md#choosing-a-window-length) for why a window that long is a poor fit for this library at all, even where `Validate()` accepts it.

**Valid range.** Greater than zero when `FailureBehavior` is `LocalFallback`. At most one day, whatever the `FailureBehavior`.

### `MaxWarmRetention`

**Meaning.** The longest time an idle partition keeps its [warm fallback state](01-concepts.md#warm-fallback-state). The real time in effect is this value or `FallbackRecoveryTime`, whichever is shorter.

**Default.** 2 minutes — a **starting point**.

**When to change it.** Shorter frees memory sooner after traffic to a key stops, at the cost of losing warm state slightly sooner. Longer keeps protection for keys that go quiet for a while and might see an outage right after traffic resumes — but rarely matters beyond `FallbackRecoveryTime`, since that value already caps the real retention time.

**Too short.** A key that has been quiet for a little while, then sees an outage, starts its fallback limiter from zero — the same gap that [warm fallback state](01-concepts.md#warm-fallback-state) exists to close.

**Too long, on its own.** Little direct harm, since `FallbackRecoveryTime` already caps the effective retention — but combined with a very large `FallbackRecoveryTime`, more idle partitions stay in memory for longer, up to the point where `MaxWarmPartitions` starts releasing warm state early for everyone on the connection.

**Valid range.** Greater than zero.

### `PolicyName`

**Meaning.** The value of the `policy` tag on every metric this limiter records. It is how you tell one policy's metrics apart from another's in [05-telemetry.md](05-telemetry.md).

**Default.** `"default"` — a **starting point**, fine for an app with exactly one policy.

**When to change it.** As soon as an app has more than one policy — name each one after what it protects, for example the group of endpoints or the resource behind it.

**Too generic (or left at the default with several policies).** Every policy's metrics merge into the same `policy` tag value, so you cannot tell which policy is rejecting requests or opening its breaker.

**Too specific — a per-request value.** A user name, an order ID, anything that changes per request, turns into a new metric time series on every request. See [05-telemetry.md](05-telemetry.md) for the cardinality problem this causes.

**Valid range.** Any string; keep it one short, fixed value per policy.

### `TagMetricsByPartitionKey` and `PartitionKey`

**Meaning.** `TagMetricsByPartitionKey` adds a `partition_key` tag, carrying the value of `PartitionKey`, to the lease metric. [`ResilientRateLimitPartition.Get`](03-api-reference.md#partition-get) fills `PartitionKey` in for you, per key, whenever `TagMetricsByPartitionKey` is `true` and `PartitionKey` is still `null` — you almost never set `PartitionKey` yourself.

**Default.** `TagMetricsByPartitionKey` is `false`, `PartitionKey` is `null` — both **starting points**.

**When to change it.** Turn `TagMetricsByPartitionKey` on only where the partition key itself is drawn from a small, fixed set of values you would happily see as separate metric time series — for example, a handful of named tenants or a fixed list of endpoint groups.

**What goes wrong if wrong — never for IP addresses.** An IP address, a user ID, or any other key with a large or unbounded set of possible values creates one permanent metric time series per value seen, and most metrics backends never forget a time series once it exists. This is the same cardinality problem as a per-request `PolicyName`, one key at a time instead of one policy at a time. See [05-telemetry.md](05-telemetry.md).

**Valid range.** `TagMetricsByPartitionKey`: `true` or `false`. `PartitionKey`: any string, or `null`; leave it `null` in almost all cases and let `ResilientRateLimitPartition.Get` fill it in.

### `MaxAddedRetryDelay`

**Meaning.** The most time the library adds on top of a Retry-After value: a random spread (see [random spread](01-concepts.md#random-spread) in the glossary) so rejected clients do not all return at the same moment, and, on a degraded path, a longer wait as well. It caps only what is **added** — never the base value itself, which can still be larger (for example, `FallbackRecoveryTime` or `BreakDuration`) than this cap. See [Retry-After](01-concepts.md#retry-after) for the full three-step calculation.

**Default.** 60 seconds — a **starting point**.

**When to change it.** Shorter if your clients retry quickly on their own and a long added wait would just make them look slower than necessary. Longer if clients poll infrequently anyway, or if you want extra spreading during a real outage.

**Zero adds nothing.** It is a valid value, not a special case: the spread and the extra degraded wait are both capped at zero, so no time is added and the client sees only the base value (still floored at one second by the last step of the Retry-After calculation).

**Too small (including zero, chosen to get an "exact" Retry-After).** Every rejected client is told the same, precise time and comes back at the same moment — which recreates the load spike the spread exists to prevent.

**Too large.** Clients wait longer than they need to before retrying, even when the store is healthy again well before the added time runs out.

**Valid range.** Zero or more (not negative).

## The fallback limiter

**The problem.** With `LocalFallback`, something has to answer while the store is down, and it has to answer in a way that still resembles a real limit — not "let everything through" or "reject everything."

**What goes wrong with the wrong kind of limiter.** A [concurrency limiter](01-concepts.md#concurrency-limiter) counts requests *in progress*, not requests *over time*: a permit comes back the moment the request's lease is disposed, usually within milliseconds. It cannot answer "how many requests has this client sent in the last minute?" because it forgets each request the instant it ends. Used as a fallback, it would let through a burst limited only by how many requests happen to be running at once — nothing like the window or bucket the primary limiter enforces.

**What the library requires.** The fallback limiter passed to `ResilientRateLimiterOptions` (or to `ResilientRateLimitPartition.Get`, or to `WithResilience`) must be a window or token-bucket limiter — `FixedWindowRateLimiter`, `SlidingWindowRateLimiter`, `TokenBucketRateLimiter`, or a custom limiter that behaves the same way. **Never a `ConcurrencyLimiter`.** This is also why the library asks you for `FallbackRecoveryTime` rather than reading it from the limiter — see that entry above for the full table of per-kind values and why no kind exposes it publicly.

**A concurrency limiter is fine as the *primary*, with `FailOpen` or `FailClosed`.** The restriction above is only about the fallback limiter used with `LocalFallback`. With `FailOpen` or `FailClosed` there is no fallback limiter at all — you pass `fallback: null` — so nothing stops the store-backed primary itself from being a concurrency-style limiter, for example `RedisConcurrencyRateLimiter` from `RedisRateLimiting`, protecting a downstream system that can only handle a few requests in flight at once. Recovery mode and warm state do not apply either way, because both exist only with `LocalFallback`.

**`AutoReplenishment = false` never refills through this library.** The built-in `FixedWindowRateLimiter`, `SlidingWindowRateLimiter` and `TokenBucketRateLimiter` all default to `AutoReplenishment = true`, which runs their own internal timer to add permits back on schedule, independent of anything this library does. This library's own idle-partition cleanup never replenishes a fallback limiter itself — it only decides how long to keep a partition's limiters alive. If you build a fallback limiter with `AutoReplenishment = false`, expecting to call `TryReplenish()` yourself, nothing in this library ever calls it for you: that fallback limiter will only ever get stricter, never recover, and warm-fallback state built on top of it stops meaning what this page assumes it means. Keep `AutoReplenishment` at its default (`true`) for any limiter you use as a fallback with this library.

## From `appsettings.json`

Only `StoreHealthOptions` binds from configuration — through the `storeHealthSection` you pass to `AddResilientRateLimiting`. `ResilientRateLimiterOptions` is never bound this way (see [Two kinds of settings](#two-kinds-of-settings)); build it in code, typically from your own configuration section if you have one (the web sample does this with its own `RateLimits` section, read into a plain `record` and then used to build `ResilientRateLimiterOptions` in code).

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

`ResilientRateLimiting:Store` binds straight to `StoreHealthOptions`, one property per key, using the same names as this page and [03-api-reference.md](03-api-reference.md). `TimeSpan` values use the `"hh:mm:ss"` (or `"hh:mm:ss.fff"`) form that the .NET configuration binder understands for `TimeSpan`. `ShouldHandle` and `OnStoreFailure` have no keys here — they are set through `configure` (see above). `ConnectionStrings` and `RateLimits` are not this library's types: `ConnectionStrings:Redis` is a plain ASP.NET Core connection string, and `RateLimits` is the sample's own settings record, used to build the Redis limiter, the fallback limiter, and `ResilientRateLimiterOptions` in code.

Next: [05-telemetry.md](05-telemetry.md) — the metrics this page's settings feed, the `LeaseSource` values, and the cardinality warning behind `TagMetricsByPartitionKey` and `PolicyName`.
