# API reference

This page explains every public type and member of the library, and each of its variations: overloads, optional parameters given or left out, and enum values. Each entry has the same parts: what it is, when you use it, its parameters, what it returns and throws, an example, common mistakes, and links to related entries.

You do not need to know rate limiting to read it, but it moves fast. If a word is new, follow its link to the [glossary](01-concepts.md#glossary) in [01-concepts.md](01-concepts.md). If you have never used the library, start with [02-getting-started.md](02-getting-started.md).

A note on numbers: every default value on this page is a **starting point**, not a measured best value, unless it says **measured** and links to [measurements.md](measurements.md). [04-configuration.md](04-configuration.md) explains why each default was chosen and when to change it.

## Contents

- [Packages and namespaces](#packages-and-namespaces) — which type is in which package
- [Wrapping a limiter](#wrapping-a-limiter) — `ResilientRateLimiter`, `WithResilience`, `ResilientRateLimitPartition.Get`
- [Store connection](#store-connection) — `StoreHealth`, `StoreHealthOptions`
- [Per-limiter settings](#per-limiter-settings) — `ResilientRateLimiterOptions`, `FallbackRecoveryTime`, `StoreFailureBehavior`
- [Sizing](#sizing) — `LocalBudget.ForReplicas`
- [Reading results](#reading-results) — `ResilientRateLimitLease`, `LeaseSource`
- [ASP.NET Core](#aspnet-core) — `UseResilientDefaults`, `AddResilientRateLimiting`, `WithLogging`
- [Exceptions you may see](#exceptions-you-may-see) — every exception, its cause and its fix

## Packages and namespaces

**The problem.** The library comes as two NuGet packages. A console app or a worker service does not need ASP.NET Core, and should not have to reference it.

**What the library does.** It splits the code by what it depends on:

| Package | Namespace | Depends on | Public types |
|---|---|---|---|
| `ResilientRateLimiting` | `ResilientRateLimiting` | `System.Threading.RateLimiting`, `Polly.Core` | `ResilientRateLimiter`, `RateLimiterResilienceExtensions` (`WithResilience`), `ResilientRateLimitPartition` (`Get`), `StoreHealth`, `StoreHealthOptions`, `ResilientRateLimiterOptions`, `StoreFailureBehavior`, `LocalBudget`, `ResilientRateLimitLease`, `LeaseSource` |
| `ResilientRateLimiting.AspNetCore` | `ResilientRateLimiting.AspNetCore` | `ResilientRateLimiting`, the ASP.NET Core shared framework | `ServiceCollectionExtensions` (`AddResilientRateLimiting`), `ResilientRateLimitingOptionsExtensions` (`UseResilientDefaults`), `StoreHealthOptionsExtensions` (`WithLogging`) |

Both packages target .NET 10. The library does not talk to Redis itself. It wraps a limiter that does, for example one from the [RedisRateLimiting](01-concepts.md#redisratelimiting) package, which you install yourself.

**When you use which.** Use only `ResilientRateLimiting` in a console app, a worker, or any code that builds its limiters by hand. Add `ResilientRateLimiting.AspNetCore` in a web app that uses the ASP.NET Core rate limiting middleware (`app.UseRateLimiter()`).

## Wrapping a limiter

**The problem.** A [primary limiter](01-concepts.md#primary-limiter) keeps its count in a shared [store](01-concepts.md#store) such as [Redis](01-concepts.md#redis), so that all [replicas](01-concepts.md#replica) of a service share one limit. When the store is slow or down, that limiter waits or throws.

**What goes wrong without a wrapper.** Every request waits for the store or fails with an exception. A problem in a helper system (the store) becomes an outage of your whole service. Measured: with Redis unreachable, a `RedisRateLimiting` limiter alone waited about 5 seconds per call and then threw (see [measurements.md](measurements.md)).

**What the library does.** It puts a decorator around the primary limiter: an object that has the same shape (it is also a `RateLimiter`) and adds behaviour. The decorator asks the store with a short time limit, counts failures in a [circuit breaker](01-concepts.md#circuit-breaker), and on a [store failure](01-concepts.md#store-failure) takes the path you chose: a local [fallback limiter](01-concepts.md#fallback-limiter), [fail open](01-concepts.md#fail-open), or [fail closed](01-concepts.md#fail-closed). The entries below are three ways to build this decorator, and its members.

<a id="resilientratelimiter"></a>
### `ResilientRateLimiter` (class)

**What it is.** `public sealed class ResilientRateLimiter : RateLimiter`. The decorator itself. It wraps one primary limiter and, optionally, one fallback limiter. Because it derives from the .NET `RateLimiter` base class, you can use it anywhere a `RateLimiter` is accepted.

**When you use it.** Usually you do not name this type. `WithResilience` and `ResilientRateLimitPartition.Get` build it for you. You name it when you call its constructor directly, or when you read `ResilientRateLimiter.MeterName`.

**Rules for the whole type.**

- **Call `AcquireAsync`, never `AttemptAcquire`.** `AttemptAcquire` always rejects (see below).
- **Do not wrap another `ResilientRateLimiter`.** Nothing stops you at run time, but it breaks two things. The inner wrapper already handles every store failure, so the outer one never sees a failure and its own fallback never runs. And the outer wrapper always tags the lease with its own [source tag](01-concepts.md#source-tag), so it hides what the inner one did: a request served by the inner fallback reaches you tagged `Distributed`.
- **Dispose only the wrapper.** It disposes the primary and the fallback limiter for you.
- **Share one instance across concurrent requests.** It is safe to call `AcquireAsync` from many requests at the same time; in a web app, all requests for one partition share one instance. Its own state changes only through atomic operations, and the counting itself happens in the primary and fallback limiters, which must also be safe for concurrent use (the .NET built-in limiters and `RedisRateLimiting`'s limiters are designed for it).

**See also.** [`WithResilience`](#withresilience), [`ResilientRateLimitPartition.Get`](#partition-get), [`StoreHealth`](#storehealth).

<a id="metername"></a>
### `ResilientRateLimiter.MeterName`

**What it is.** `public const string MeterName = "ResilientRateLimiting";`. The name of the .NET `Meter` that records the library's metrics. A meter is a named group of measurements; a metrics system such as OpenTelemetry collects only the meters you name.

**When you use it.** Once, at startup, when you connect metrics collection.

**Returns.** The constant string `"ResilientRateLimiting"`.

**Example.**

<!-- snippet: web-meter -->
```csharp
builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter(ResilientRateLimiter.MeterName));
```

From `samples/ResilientRateLimiting.Samples.Web/Program.cs`

**Common mistakes.**

- Typing the name as a string literal. It works until the name changes; the constant keeps the two in step.
- Forgetting `AddMeter` entirely. Nothing fails, but no metric is exported, so you cannot see when your service runs on the fallback.

**See also.** [05-telemetry.md](05-telemetry.md) for the instruments and their tags.

<a id="constructor"></a>
### `new ResilientRateLimiter(primary, fallback, options, storeHealth, timeProvider = null)`

**What it is.** The constructor. Full signature: `ResilientRateLimiter(RateLimiter primary, RateLimiter? fallback, ResilientRateLimiterOptions options, StoreHealth storeHealth, TimeProvider? timeProvider = null)`. It checks its arguments, validates `options`, and registers the new limiter as one live partition on `storeHealth`.

**When you use it.** When you prefer a constructor call to the `WithResilience` extension method. Both do the same thing; `WithResilience` only calls this constructor.

**Parameters.**

| Name | Type | Required | Default | Meaning | Valid values |
|---|---|---|---|---|---|
| `primary` | `RateLimiter` | yes | — | The limiter backed by the shared store. | Not `null`. Not another `ResilientRateLimiter`. |
| `fallback` | `RateLimiter?` | only with `LocalFallback` | — | The in-memory limiter that answers when the store fails. It also keeps [warm fallback state](01-concepts.md#warm-fallback-state). | A fixed window, sliding window or token-bucket limiter. Never a `ConcurrencyLimiter`: it gives its permit back when the lease is disposed, so it cannot remember recent traffic. May be `null` with `FailOpen` or `FailClosed`. |
| `options` | `ResilientRateLimiterOptions` | yes | — | Settings for this one limiter. | Not `null`. Must pass [`Validate()`](#limiter-options-validate). |
| `storeHealth` | `StoreHealth` | yes | — | The shared health of the store connection. | Not `null`. The same instance for every limiter on the same [store connection](01-concepts.md#store-connection). |
| `timeProvider` | `TimeProvider?` | no | `TimeProvider.System` | The clock this limiter uses for the store timeout, recovery mode, warm retention and idle time. | Any `TimeProvider`. Tests pass a fake clock. |

If you pass a fallback while `FailureBehavior` is `FailOpen` or `FailClosed`, the fallback is never asked, but it is still disposed with the wrapper.

**Throws.**

| Exception | Exact cause |
|---|---|
| `ArgumentNullException` | `primary`, `options` or `storeHealth` is `null`; or `fallback` is `null` while `options.FailureBehavior` is `LocalFallback`. |
| `InvalidOperationException` | `options` fails `Validate()`. The message lists every broken rule, one per line. Validation runs before the `fallback` check, so if both are wrong you see this one first. |

**Example.**

<!-- snippet: constructor -->
```csharp
using var limiter = new ResilientRateLimiter(primary, fallback, options, storeHealth);
```

From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

**Common mistakes.**

- **`using` on `primary` or `fallback`.** The wrapper owns them. If your own `using` disposes one first, the next request gets an `ObjectDisposedException`. That exception counts as a caller mistake, not a store failure, so it reaches your code instead of the fallback.
- **A new `StoreHealth` per limiter.** See [`StoreHealth`](#storehealth) for the three things this breaks.
- **One fallback instance shared by two wrappers.** The two limiters then share one local budget, and the first wrapper you dispose also disposes the other one's fallback.
- **A fake `TimeProvider` here but not on `StoreHealth`.** The circuit breaker runs on the `StoreHealth` clock and the store timeout on this one. In a test, the two clocks then disagree. Pass the same clock to both (see the [`StoreHealth` constructor](#storehealth-constructor) example).

**See also.** [`WithResilience`](#withresilience), [`ResilientRateLimiterOptions`](#limiter-options), [`StoreFailureBehavior`](#storefailurebehavior).

<a id="acquireasync"></a>
### `AcquireAsync(permitCount = 1, cancellationToken = default)`

**What it is.** The method you call for every request. It is inherited from `RateLimiter` as `ValueTask<RateLimitLease> AcquireAsync(int permitCount = 1, CancellationToken cancellationToken = default)`; the wrapper supplies its behaviour. The steps are:

1. **Recovery gate.** If the limiter is in [recovery mode](01-concepts.md#recovery-mode) (the store came back after an outage a short time ago), the request is first charged to the local counter. If the local counter refuses, the request is rejected at once, without a store call, with the source `Recovery`.
2. **Store call.** The wrapper asks the primary limiter, through the circuit breaker, and waits at most `StoreHealthOptions.StoreTimeout`. If the store answers in time, its answer is used, allowed or rejected, with the source `Distributed`. A rejection by the store is a normal answer, not a failure. With `LocalFallback`, an allowed request is also charged to the fallback limiter, so that it stays warm.
3. **Fallback path.** If the store call fails (it throws a store failure, takes longer than `StoreTimeout`, or the breaker is open), the wrapper reports the failure to `OnStoreFailure` and to the metrics, then answers from the path set by `FailureBehavior`: the fallback limiter (`LocalFallback`), always allow (`FailOpen`), or always reject (`FailClosed`).

**When you use it.** For every request you want to limit. In ASP.NET Core the rate limiting middleware calls it for you.

**Parameters.**

| Name | Type | Required | Default | Meaning | Valid values |
|---|---|---|---|---|---|
| `permitCount` | `int` | no | `1` | How many [permits](01-concepts.md#permit) this request needs. | 0 or more, and not above the primary limiter's own limit (see Throws). |
| `cancellationToken` | `CancellationToken` | no | `default` | Stops the wait for the store. | Any token. |

**Returns.** A [lease](01-concepts.md#lease), always of type `ResilientRateLimitLease`. Check `lease.IsAcquired`. The lease carries the source tag (which path answered) and, on a rejection, often a [Retry-After](01-concepts.md#retry-after-header) value. Dispose the lease when the request ends.

**Throws.** Exceptions that are not store failures reach you, unchanged:

| Exception | Exact cause |
|---|---|
| `OperationCanceledException` | Your `cancellationToken` was cancelled. Cancellation is never a store failure, so the fallback is not used. |
| `ArgumentException` (including `ArgumentOutOfRangeException`) | The primary or fallback limiter refused `permitCount`. Every `RedisRateLimiting` limiter throws `ArgumentOutOfRangeException` when `permitCount` is above its own limit. |
| `ObjectDisposedException` | The primary or fallback limiter was already disposed. |
| `InvalidOperationException` | The primary or fallback limiter threw it. |

These three default rules apply only while `StoreHealthOptions.ShouldHandle` is `null`; a `ShouldHandle` predicate can change them. An exception thrown by the fallback limiter itself always reaches you: there is no further path to try.

**Example.** Reading which path answered:

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

**Common mistakes.**

- **Asking for more permits than the store limiter allows.** You get an `ArgumentOutOfRangeException`, not a rejected lease. Check the count before you call:

<!-- snippet: permit-count-check -->
```csharp
// ArgumentOutOfRangeException here is a programming error, not a store failure, so check first instead of relying on it.
if (permitCount > StorePermitLimit)
{
    Console.WriteLine($"Rejected locally: {permitCount} exceeds the store limit of {StorePermitLimit}.");
}
else
{
    using var lease = await limiter.AcquireAsync(permitCount);
    Console.WriteLine($"Allowed: {lease.IsAcquired}");
}
```

From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

- **Treating that exception as a store failure through `ShouldHandle`.** `ShouldHandle` also feeds the circuit breaker. A client that keeps sending a too-large count would then open the breaker and push every request on the connection to the fallback.
- **Not disposing the lease.** Disposing every `RateLimitLease` is the rule. Some limiters give permits back only when the lease is disposed, so a lease you forget can hold a permit.
- **Expecting the store call to be free when it times out.** The wrapper stops waiting after `StoreTimeout`, but a store call that already left the process may still be counted by the store. See [06-production.md](06-production.md).

**See also.** [`StoreHealthOptions`](#storehealthoptions) (`StoreTimeout`, `ShouldHandle`), [`StoreFailureBehavior`](#storefailurebehavior), [Exceptions you may see](#exceptions-you-may-see).

<a id="attemptacquire"></a>
### `AttemptAcquire(permitCount = 1)` — always rejects

**What it is.** The synchronous method from `RateLimiter`. On this type it always returns a rejected lease, with no source tag. It never calls the store.

**Why.** A store call takes time over the network, and a synchronous method cannot wait for it without blocking a thread. Without the store, nothing can decide honestly, so the method says "no" and adds no source tag.

**When you use it.** Never on purpose. The ASP.NET Core middleware calls it first and, when it is rejected, calls `AcquireAsync`, so the middleware works correctly.

**Parameters.** `permitCount` (`int`, default `1`): ignored.

**Returns.** A lease with `IsAcquired == false` and no metadata.

**Example.**

<!-- snippet: acquire-async-not-attempt -->
```csharp
var attempted = limiter.AttemptAcquire(1);
Console.WriteLine($"AttemptAcquire always rejects: {!attempted.IsAcquired}");

using var lease = await limiter.AcquireAsync(1);
Console.WriteLine($"AcquireAsync allowed: {lease.IsAcquired}");
```

From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

**Common mistakes.** Calling `AttemptAcquire` in your own code. Every request is rejected, and the rejection has no source tag, so it looks like none of the library's paths.

**See also.** [`AcquireAsync`](#acquireasync).

<a id="getstatistics"></a>
### `GetStatistics()` — always `null`

**What it is.** From `RateLimiter`. It returns live counters such as available permits. On this type it always returns `null`.

**Why.** The wrapper keeps no counters of its own. The shared count lives in the store, and a local number would be wrong for a limit shared by many replicas.

**When you use it.** Not on this type. Use the metrics instead ([05-telemetry.md](05-telemetry.md)).

**Returns.** `null`. **Throws.** Nothing.

**Example.** None: the method always returns `null`, so there is nothing to show.

**Common mistakes.** Code that reads `GetStatistics()!.CurrentAvailablePermits` gets a `NullReferenceException`.

**See also.** [`IdleDuration`](#idleduration), [05-telemetry.md](05-telemetry.md).

<a id="idleduration"></a>
### `IdleDuration`

**What it is.** A property from `RateLimiter`, `TimeSpan? IdleDuration`. `PartitionedRateLimiter` reads it to find [partitions](01-concepts.md#partition) that nobody uses, and removes their limiters to free memory. `null` means "not idle, keep me".

The wrapper returns:

- `null` while a request is in progress;
- `null` while the fallback limiter holds [warm fallback state](01-concepts.md#warm-fallback-state), unless the number of live partitions on the store connection is above `StoreHealthOptions.MaxWarmPartitions`;
- otherwise, the time since this limiter last served a request (or since it was created, if it has served none).

Warm state is held for at most `MaxWarmRetention` or `FallbackRecoveryTime`, whichever is shorter (see [Warm state](01-concepts.md#warm-state)).

**When you use it.** You do not read it yourself. It is how the wrapper keeps a partition alive just long enough.

**Example.** Take a partition with `FailureBehavior = LocalFallback`, `FallbackRecoveryTime` = 1 minute and the default `MaxWarmRetention` of 2 minutes, so warm state is kept for 1 minute (the shorter of the two). Its last request was allowed by the store, so that request was also charged to the fallback limiter.

- 30 seconds later, with no request in progress: `IdleDuration` is `null`. The fallback still holds warm state, so `PartitionedRateLimiter` keeps the partition.
- 90 seconds later: warm state is older than 1 minute, so `IdleDuration` is about 1 minute 30 seconds, and `PartitionedRateLimiter` may remove the partition.

With `FailOpen` or `FailClosed` the fallback is never charged, so `IdleDuration` is simply the time since the last request.

**Common mistakes.** Wrapping a primary limiter whose partitions you expect to be removed at once. With `LocalFallback`, an idle partition stays in memory for up to the warm retention time. That is intended; the cost per partition is about 1 KB (**measured**, see [measurements.md](measurements.md#memory-per-partition)).

**See also.** [`Dispose()` and `DisposeAsync()`](#dispose), [`ResilientRateLimiterOptions`](#limiter-options) (`MaxWarmRetention`), [`StoreHealthOptions`](#storehealthoptions) (`MaxWarmPartitions`), [Warm state](01-concepts.md#warm-state).

<a id="dispose"></a>
### `Dispose()` and `DisposeAsync()`

**What it is.** From `RateLimiter`. Disposing the wrapper:

1. lowers the live-partition count on its `StoreHealth` (only once, even if you dispose twice);
2. disposes the primary limiter;
3. disposes the fallback limiter, if one was passed.

`DisposeAsync()` does the same, and awaits the asynchronous disposal of both limiters.

**When you use it.** When the limiter is no longer needed. A `using` or `await using` declaration does it for you. For partitions, `PartitionedRateLimiter` disposes each partition's limiter when it removes it, and all of them when it is disposed itself.

**Returns / throws.** Nothing of its own. An exception from the primary or fallback limiter's own disposal reaches you.

**Example.**

<!-- snippet: dispose -->
```csharp
var primary = new InMemoryStore(permitLimit: 5, window: TimeSpan.FromSeconds(10));
var fallback = new InMemoryStore(permitLimit: 2, window: TimeSpan.FromSeconds(10));

// The wrapper disposes its primary and fallback, so only the wrapper needs disposing here.
using var limiter = primary.WithResilience(fallback, options, storeHealth);
```

From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

The asynchronous form:

<!-- snippet: dispose-async -->
```csharp
await using var limiter = primary.WithResilience(fallback, options, storeHealth);
```

From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

**Common mistakes.**

- **Not disposing wrappers you build by hand.** The live-partition count only falls on disposal. It then grows forever, passes `MaxWarmPartitions`, and warm state is released early for every partition on the connection.
- **Disposing the primary or fallback limiter yourself** as well. Only the wrapper should be disposed.

**See also.** [`StoreHealthOptions.MaxWarmPartitions`](#storehealthoptions).

<a id="withresilience"></a>
### `RateLimiterResilienceExtensions.WithResilience(this primary, fallback, options, storeHealth, timeProvider = null)`

**What it is.** An extension method on `RateLimiter`: `public static RateLimiter WithResilience(this RateLimiter primary, RateLimiter? fallback, ResilientRateLimiterOptions options, StoreHealth storeHealth, TimeProvider? timeProvider = null)`. It builds a `ResilientRateLimiter` around `primary`. It is the same as calling the [constructor](#constructor); it only reads better.

**When you use it.** For one limiter that is not partitioned: one shared limit for all callers. For a limit per client or per key, use [`ResilientRateLimitPartition.Get`](#partition-get).

**Parameters.** Same meaning and rules as the [constructor](#constructor):

| Name | Type | Required | Default | Meaning | Valid values |
|---|---|---|---|---|---|
| `primary` | `RateLimiter` | yes | — | The store-backed limiter (the `this` argument). | Not `null`; not a `ResilientRateLimiter`. |
| `fallback` | `RateLimiter?` | only with `LocalFallback` | — | The in-memory fallback limiter. | Window or token bucket; `null` only with `FailOpen` or `FailClosed`. |
| `options` | `ResilientRateLimiterOptions` | yes | — | Settings for this limiter. | Must pass `Validate()`. |
| `storeHealth` | `StoreHealth` | yes | — | Shared store health. | One per store connection. |
| `timeProvider` | `TimeProvider?` | no | `TimeProvider.System` | The clock. | Any `TimeProvider`. |

**Returns.** A `RateLimiter`. Its real type is `ResilientRateLimiter`; you rarely need to cast it.

**Throws.** Same as the [constructor](#constructor): `ArgumentNullException` for a `null` `primary`, `options` or `storeHealth`, or a `null` `fallback` with `LocalFallback`; `InvalidOperationException` when `options` fails validation.

**Variation: fallback given (the default `LocalFallback`).**

<!-- snippet: with-resilience -->
```csharp
using var limiter = primary.WithResilience(fallback, options, storeHealth);
```

From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

**Variation: fallback `null`.** Allowed only with `FailOpen` or `FailClosed`. Name the argument, so a reader sees that `null` is on purpose:

<!-- snippet: fail-closed -->
```csharp
var options = new ResilientRateLimiterOptions { FailureBehavior = StoreFailureBehavior.FailClosed };

using var limiter = primary.WithResilience(fallback: null, options, storeHealth);
```

From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

**Variation: `timeProvider` given.** Tests pass a fake clock, so that timeouts and recovery can be checked without real waiting. Left out, the limiter uses `TimeProvider.System`, the real clock.

<!-- snippet: time-provider -->
```csharp
var timeProvider = TimeProvider.System; // tests pass a fake TimeProvider instead

using var limiter = primary.WithResilience(fallback, options, storeHealth, timeProvider);
```

From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

**Common mistakes.**

- **`fallback: null` with default options.** The default behaviour is `LocalFallback`, so this throws `ArgumentNullException`. Set `FailureBehavior` too.
- **`using` on the limiters you pass in.** The wrapper disposes them; your `using` would dispose them while the wrapper still needs them.
- **Calling it once per request.** Each call builds a new wrapper with its own recovery state and adds one live partition to `StoreHealth`. Build the limiter once and keep it.

**See also.** [`ResilientRateLimitPartition.Get`](#partition-get), [`StoreFailureBehavior`](#storefailurebehavior).

<a id="partition-get"></a>
### `ResilientRateLimitPartition.Get<TKey>(partitionKey, primaryFactory, fallbackFactory, options, storeHealth, timeProvider = null)`

**What it is.** `public static RateLimitPartition<TKey> Get<TKey>(TKey partitionKey, Func<TKey, RateLimiter> primaryFactory, Func<TKey, RateLimiter>? fallbackFactory, ResilientRateLimiterOptions options, StoreHealth storeHealth, TimeProvider? timeProvider = null)`. It describes one [partition](01-concepts.md#partition) whose limiter is a `ResilientRateLimiter`. A partition is one separate count per [partition key](01-concepts.md#partition-key), for example one count per client. You return this value from the function you pass to `PartitionedRateLimiter.Create`, or to an ASP.NET Core rate limiting policy.

**When you use it.** When each client, user or tenant needs its own limit. It does the same as the built-in `RateLimitPartition.GetFixedWindowLimiter` and similar methods, but the limiter it creates is resilient.

**How the factories are called.** `Get` itself only builds a small description of the partition. `PartitionedRateLimiter` calls the function you gave it on every request, but it calls the factory inside the partition only the first time it sees a key. At that moment `primaryFactory(key)` and `fallbackFactory(key)` each run once, and the resulting wrapper serves every later request for that key. If the partition was idle long enough to be removed and the key comes back, the factories run again for a new wrapper.

**Parameters.**

| Name | Type | Required | Default | Meaning | Valid values |
|---|---|---|---|---|---|
| `partitionKey` | `TKey` | yes | — | The key this partition counts for. | Any value. Its `ToString()` is used as a metric tag when `TagMetricsByPartitionKey` is on. |
| `primaryFactory` | `Func<TKey, RateLimiter>` | yes | — | Builds the store-backed limiter for a key. | Not `null`. Must return a new limiter each time. |
| `fallbackFactory` | `Func<TKey, RateLimiter>?` | only with `LocalFallback` | — | Builds the key's own in-memory fallback limiter. | Returns a new window or token-bucket limiter each time, sized with [`LocalBudget.ForReplicas`](01-concepts.md#local-budget), not a literal. `null` only with `FailOpen` or `FailClosed`. |
| `options` | `ResilientRateLimiterOptions` | yes | — | Settings, shared by every partition. | Must pass `Validate()`. |
| `storeHealth` | `StoreHealth` | yes | — | Shared store health. | The same instance for every partition, never one per partition. |
| `timeProvider` | `TimeProvider?` | no | `TimeProvider.System` | The clock for every partition's limiter. | Any `TimeProvider`. |

**Returns.** A `RateLimitPartition<TKey>`.

**Throws.**

| Exception | When | Exact cause |
|---|---|---|
| `ArgumentNullException` | at the call | `primaryFactory`, `options` or `storeHealth` is `null`. |
| `ArgumentNullException` | at the first request for a key | `fallbackFactory` is `null` while `FailureBehavior` is `LocalFallback`. |
| `InvalidOperationException` | at the first request for a key | `options` fails `Validate()`. |

The last two come late because the wrapper is built only when a key is first used. To fail at startup instead, call `options.Validate()` yourself when the app starts.

**Variation: with a fallback factory.**

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

**Variation: `fallbackFactory` `null`.** With `FailOpen` or `FailClosed` there is no local limiter:

<!-- snippet: partition-no-fallback -->
```csharp
var options = new ResilientRateLimiterOptions { FailureBehavior = StoreFailureBehavior.FailClosed };

using var limiter = PartitionedRateLimiter.Create<string, string>(key =>
    ResilientRateLimitPartition.Get(
        key,
        partitionKey => new UnreachableStore(),
        fallbackFactory: null,
        options,
        storeHealth));
```

From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

**Variation: metrics per partition key.** `TagMetricsByPartitionKey` on [`ResilientRateLimiterOptions`](#limiter-options) decides whether the lease metric gets a `partition_key` tag:

| `TagMetricsByPartitionKey` | `PartitionKey` | Tag on each partition's metrics |
|---|---|---|
| `false` (default) | anything | no `partition_key` tag |
| `true` | `null` (default) | `Get` fills in each key's own `ToString()`, so each key gets its own tag value |
| `true` | a fixed string | that same string for every partition, because `Get` fills in the key only while `PartitionKey` is `null` |

<!-- snippet: partition-metrics-tag -->
```csharp
var options = new ResilientRateLimiterOptions
{
    FallbackRecoveryTime = TimeSpan.FromMinutes(1),
    TagMetricsByPartitionKey = true,
};

using var limiter = PartitionedRateLimiter.Create<string, string>(key =>
    ResilientRateLimitPartition.Get(
        key,
        partitionKey => new InMemoryStore(permitLimit: 5, window: TimeSpan.FromSeconds(10)),
        partitionKey => new InMemoryStore(permitLimit: 2, window: TimeSpan.FromSeconds(10)),
        options,
        storeHealth));
```

From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

**Variation: `timeProvider` given.** Same as for [`WithResilience`](#withresilience): every partition's limiter uses that clock.

**Common mistakes.**

- **A factory that returns the same limiter object every time.** All keys then share one count, and when one partition is removed, its disposal disposes the limiter the other keys still use. Build a new limiter inside the factory.
- **`new StoreHealth(...)` inside the partition function.** One `StoreHealth` per partition breaks the circuit breaker and the partition count (see [`StoreHealth`](#storehealth)). Create it once, outside.
- **Expensive work in the function passed to `PartitionedRateLimiter.Create`, outside the factories.** That function runs on every request. Only the factories run once per key.
- **`TagMetricsByPartitionKey = true` for an unbounded key** such as an IP address or a user id. Each value becomes a new metric time series, and the number never stops growing.
- **A fixed `PartitionKey` with `TagMetricsByPartitionKey = true`.** Every partition reports the same tag value; this is rarely what you want.

**See also.** [`WithResilience`](#withresilience), [Partitions](01-concepts.md#partitions), [05-telemetry.md](05-telemetry.md).

## Store connection

**The problem.** Many limiters use the same store: one per partition, often thousands. When the store fails, it fails for all of them together. Someone has to notice that it failed, decide to stop asking it for a while, and report it, once for the whole store.

**What goes wrong if each limiter decides alone.** Each partition sees only its own few requests. No single partition sees enough failures to decide the store is down, so every request keeps waiting for the store timeout. Every partition also reports the same outage again.

**What the library does.** It keeps everything that belongs to one [store connection](01-concepts.md#store-connection) in one object, `StoreHealth`, with its settings in `StoreHealthOptions`. You create one and pass the same instance to every limiter that uses that connection.

<a id="storehealth"></a>
### `StoreHealth` (class)

**What it is.** `public sealed class StoreHealth`. The shared health of one store connection. It holds the circuit breaker, the rule that decides what counts as a store failure, the count of live partitions, whether the store has been reached since startup (for [cold start](01-concepts.md#cold-start)), and the repeat rules for failure reports. It has no public members apart from its constructor; you only create it and pass it on.

**When you use it.** Once per store connection: once per Redis connection in a typical app. In ASP.NET Core, `AddResilientRateLimiting` registers one for you (see the ASP.NET Core section below).

**One per connection, never one per partition.** If each partition gets its own `StoreHealth`, three things break, silently:

- **The circuit breaker never opens.** Each instance sees only its own partition's traffic, which is rarely enough calls to reach `FailuresBeforeOpen`. During an outage every request waits for the full store timeout.
- **The live-partition count is wrong.** Each instance counts only the one partition that holds it, so the `MaxWarmPartitions` limit never applies, and memory for warm state is not capped.
- **Failure reports repeat.** `OnStoreFailure` reports the first failure once per instance, so one outage produces one report per partition instead of one for the store.

Two different store connections (for example two Redis servers) need two instances: each has its own health.

**Disposal.** `StoreHealth` holds nothing that needs disposing and is not disposable. Only the limiters that use it need disposing.

**See also.** [`StoreHealthOptions`](#storehealthoptions), [The circuit breaker](01-concepts.md#the-circuit-breaker).

<a id="storehealth-constructor"></a>
### `new StoreHealth(options, timeProvider = null)`

**What it is.** `public StoreHealth(StoreHealthOptions options, TimeProvider? timeProvider = null)`. It validates `options`, then builds the circuit breaker from them. The options are read once, here; later changes are not possible (the record has only `init` properties) and would not be seen.

**When you use it.** At startup, once per store connection.

**Parameters.**

| Name | Type | Required | Default | Meaning | Valid values |
|---|---|---|---|---|---|
| `options` | `StoreHealthOptions` | yes | — | Settings for this store connection. | Not `null`. Must pass [`Validate()`](#storehealthoptions-validate). |
| `timeProvider` | `TimeProvider?` | no | `TimeProvider.System` | The clock for the circuit breaker and for the failure-report repeat rules. | Any `TimeProvider`. |

**Throws.**

| Exception | Exact cause |
|---|---|
| `ArgumentNullException` | `options` is `null`. |
| `InvalidOperationException` | `options` fails `Validate()`. The message lists every broken rule, one per line. |

Because it validates here, a wrong setup fails at startup, not on the first request.

**Variation: default options, clock left out.**

<!-- snippet: store-health -->
```csharp
var storeHealth = new StoreHealth(new StoreHealthOptions
{
    StoreTimeout = TimeSpan.FromMilliseconds(200),
    FailuresBeforeOpen = 5,
    BreakDuration = TimeSpan.FromSeconds(5),
});
```

From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

(The values here are sample values for a demo, not advice.)

**Variation: `timeProvider` given.** Pass the same clock to `StoreHealth` and to every limiter that uses it:

<!-- snippet: store-health-time-provider -->
```csharp
var timeProvider = TimeProvider.System; // tests pass a fake TimeProvider instead

var storeHealth = new StoreHealth(new StoreHealthOptions(), timeProvider);
using var limiter = primary.WithResilience(fallback, options, storeHealth, timeProvider);
```

From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

**Common mistakes.**

- One instance per partition or per limiter (see [`StoreHealth`](#storehealth)).
- Two unkeyed `StoreHealth` registrations in dependency injection for two connections. The last registration wins, so both policies get the same instance. Give the second connection its own hand-built instance, captured by its own policy.
- A fake clock on the limiters but not on `StoreHealth` (or the other way round): in tests, the breaker and the timeout then run on different clocks.

**See also.** [`StoreHealthOptions`](#storehealthoptions).

<a id="storehealthoptions"></a>
### `StoreHealthOptions` (record)

**What it is.** `public sealed record StoreHealthOptions`. The settings for one store connection, shared by every limiter that uses it. It is a record with `init` properties: you set values when you create it, and you cannot change them later.

**When you use it.** To create a [`StoreHealth`](#storehealth-constructor). In ASP.NET Core, it is bound from configuration by `AddResilientRateLimiting`.

**Properties.** "Default" values are **starting points**; [04-configuration.md](04-configuration.md) explains each one.

| Property | Type | Default | Meaning | Valid values |
|---|---|---|---|---|
| `StoreTimeout` | `TimeSpan` | 20 ms | How long to wait for the store before the call counts as a failure and the fallback path answers. See [store timeout](01-concepts.md#store-timeout). | Greater than zero. |
| `FailuresBeforeOpen` | `int` | 5 | How many store calls must happen within `BreakerSamplingDuration` before the breaker may open. See [failures before open](01-concepts.md#failures-before-open). | At least 2. |
| `FailureRatio` | `double` | 0.5 | The share of those calls that must fail. The breaker opens when at least `FailuresBeforeOpen` calls happened **and** the failed share is at least this value; exactly half failing opens it with the default. See [failure ratio](01-concepts.md#failure-ratio). | Greater than 0, at most 1. A value of 1 means every call must fail. |
| `BreakDuration` | `TimeSpan` | 5 s | How long the breaker stays open (no store calls) before it tries the store again. See [break duration](01-concepts.md#break-duration). | Greater than zero. |
| `BreakerSamplingDuration` | `TimeSpan` | 10 s | The time window in which calls and failures are counted. It is also the quiet time after which `OnStoreFailure` reports the same exception type again. See [breaker sampling duration](01-concepts.md#breaker-sampling-duration). | At least 500 ms; the breaker library underneath refuses a shorter window. |
| `MaxWarmPartitions` | `int` | 10,000 | When more partitions than this are alive on this connection, warm state is released early for **every** partition, not only the extra ones. | At least 1. |
| `ColdStartFallbackFactor` | `double` | 1.0 | Until any limiter on this connection has reached the store once, a request for N permits is charged N ÷ factor permits (rounded up) on the local fallback. 1.0 means no effect. See [cold start](01-concepts.md#cold-start). | Greater than 0, at most 1. |
| `ShouldHandle` | `Func<Exception, bool>?` | `null` | Decides whether an exception thrown by the store limiter counts as a store failure (see below). | Any predicate, or `null`. |
| `OnStoreFailure` | `Action<Exception>?` | `null` | Called when a store failure happens (see below for how often). | Any callback, or `null`. |

**How an exception is classified.** When the store limiter throws, the library decides in this order:

1. Caller cancellation (`OperationCanceledException`) is **never** a store failure. You cannot change this.
2. The library's own timeout (the store did not answer within `StoreTimeout`) and its own open-breaker exception are **always** store failures. You cannot change this.
3. If `ShouldHandle` is set, its answer decides. If it returns `false` for an exception it does not recognise, that exception reaches the caller instead of starting the fallback path.
4. If `ShouldHandle` is `null`: `ArgumentException`, `ObjectDisposedException` and `InvalidOperationException` reach the caller (they point to a programming mistake), and every other exception is a store failure.

The same answer also feeds the circuit breaker: every exception classified as a store failure counts toward opening it.

**When `OnStoreFailure` is called.** For the first failure of each exception type, and again for that type once it has been quiet for `BreakerSamplingDuration`, or once the breaker has closed since that type last failed. So one outage produces a few reports, not one per request. The callback runs on the request path, so keep it fast. If it throws, the exception is caught and ignored: a handled store failure must not become an unhandled one.

**Example.**

<!-- snippet: store-failure-callback -->
```csharp
var storeHealth = new StoreHealth(new StoreHealthOptions
{
    OnStoreFailure = exception => Console.WriteLine($"Store failure: {exception.GetType().Name}"),
    ShouldHandle = exception => exception is IOException,
});
```

From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

**Copies with `with`.** Because `StoreHealthOptions` is a record, `baseOptions with { StoreTimeout = ... }` makes a copy with one value changed and leaves the original as it was. The copy shares the same `ShouldHandle` and `OnStoreFailure` delegates. The pattern is the same as for [`ResilientRateLimiterOptions`](#limiter-options) (see the `options-copy` example there).

**Common mistakes.**

- **A `ShouldHandle` that returns `false` for everything it does not know.** Real store errors (network, timeouts in the Redis client) then reach your callers instead of the fallback. Return `true` for store errors; only return `false` for exceptions you know are mistakes.
- **`ShouldHandle` returning `true` for `ArgumentOutOfRangeException`.** A too-large permit count then counts as a store failure and can open the breaker for everyone (see [`AcquireAsync`](#acquireasync)).
- **A Redis client timeout much longer than `StoreTimeout`.** The wrapper stops waiting after `StoreTimeout`, but the abandoned call stays open in the Redis client until the client's own timeout. See [06-production.md](06-production.md) for how to set the two.
- **Slow work in `OnStoreFailure`**, such as a blocking network call. It delays the request that hit the failure.

**See also.** [`StoreHealth`](#storehealth), [04-configuration.md](04-configuration.md), [05-telemetry.md](05-telemetry.md).

<a id="storehealthoptions-validate"></a>
### `StoreHealthOptions.Validate()`

**What it is.** `public void Validate()`. It checks every rule in the Valid values column above and throws if any is broken. The `StoreHealth` constructor calls it for you.

**When you use it.** Rarely by hand. Call it yourself if you build options early and want the error before you create the `StoreHealth`.

**Returns.** Nothing, when the options are valid.

**Throws.** `InvalidOperationException` when at least one rule is broken. The message lists **every** broken rule, one per line, not only the first. Each line names the property, for example `FailuresBeforeOpen must be at least 2. ...`.

**Example.**

<!-- snippet: store-health-validate -->
```csharp
var invalid = new StoreHealthOptions
{
    FailuresBeforeOpen = 1,
    BreakerSamplingDuration = TimeSpan.FromMilliseconds(100),
};

try
{
    _ = new StoreHealth(invalid);
}
catch (InvalidOperationException exception)
{
    Console.WriteLine(exception.Message);
}
```

From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

It prints:

```text
FailuresBeforeOpen must be at least 2. The underlying breaker cannot open on a single failure.
BreakerSamplingDuration must be at least 500 ms.
```

**Common mistakes.** Catching this exception and continuing with default options. The app then runs with settings nobody chose. Let it stop the startup.

## Per-limiter settings

**The problem.** Some settings belong to the store connection and must be the same for every limiter on it (they live on `StoreHealthOptions`). Others belong to one limiter or one policy: what to do when the store fails, how long the fallback needs to refill, what name to use in metrics.

**What the library does.** It keeps the per-limiter settings in `ResilientRateLimiterOptions`, and the choice of failure path in the `StoreFailureBehavior` enum.

<a id="limiter-options"></a>
### `ResilientRateLimiterOptions` (record)

**What it is.** `public sealed record ResilientRateLimiterOptions`. Settings for one `ResilientRateLimiter`, or for every partition of one policy. A record with `init` properties.

**When you use it.** Every time you build a wrapper: in the constructor, `WithResilience`, or `ResilientRateLimitPartition.Get`.

**Properties.** "Default" values are **starting points**; [04-configuration.md](04-configuration.md) explains each one.

| Property | Type | Default | Meaning | Valid values |
|---|---|---|---|---|
| `FailureBehavior` | `StoreFailureBehavior` | `LocalFallback` | Which path answers when the store fails. See [`StoreFailureBehavior`](#storefailurebehavior). | `LocalFallback`, `FailOpen`, `FailClosed`. |
| `FallbackRecoveryTime` | `TimeSpan` | zero (not set) | How long the fallback limiter needs to refill completely. See [`FallbackRecoveryTime`](#fallbackrecoverytime). | With `LocalFallback`: required, greater than zero. With every behaviour: at most `MaxFallbackRecoveryTime` (1 day). |
| `MaxWarmRetention` | `TimeSpan` | 2 minutes | The longest time an idle partition keeps its [warm fallback state](01-concepts.md#warm-fallback-state). The real time is this or `FallbackRecoveryTime`, whichever is shorter. See [warm retention](01-concepts.md#warm-retention). | Greater than zero. |
| `PolicyName` | `string` | `"default"` | The value of the `policy` tag on every metric of this limiter. | One short, fixed name per policy, for example the group of endpoints it protects. Never a value that changes per request. |
| `TagMetricsByPartitionKey` | `bool` | `false` | Adds a `partition_key` tag to the lease metric. | `true` only for keys with a small, fixed set of values. |
| `PartitionKey` | `string?` | `null` | The value used for the `partition_key` tag, only when `TagMetricsByPartitionKey` is `true`. `ResilientRateLimitPartition.Get` fills it with each key while it is `null`. | Leave `null` in almost all cases. |
| `MaxAddedRetryDelay` | `TimeSpan` | 60 s | The most time the library adds to a [Retry-After](01-concepts.md#retry-after-header) value: a [random spread](01-concepts.md#random-spread) so rejected callers do not all come back at the same moment, and, while the store is failing, a longer wait. The total added never exceeds this. Zero adds nothing. | Zero or more. |

**Example: every property set.**

<!-- snippet: limiter-options-all -->
```csharp
var options = new ResilientRateLimiterOptions
{
    FailureBehavior = StoreFailureBehavior.LocalFallback,
    FallbackRecoveryTime = TimeSpan.FromSeconds(10),
    MaxWarmRetention = TimeSpan.FromMinutes(2),
    PolicyName = "orders-api",
    TagMetricsByPartitionKey = false,
    MaxAddedRetryDelay = TimeSpan.FromSeconds(60),
};
```

From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

(`FallbackRecoveryTime` is 10 seconds here because the sample's fallback limiter has a 10-second window.)

**Copies with `with`.** Several policies often share most settings. Build one base value and copy it:

<!-- snippet: options-copy -->
```csharp
var baseOptions = new ResilientRateLimiterOptions { FallbackRecoveryTime = TimeSpan.FromMinutes(1) };

var ordersOptions = baseOptions with { PolicyName = "orders-api" };
var paymentsOptions = baseOptions with { PolicyName = "payments-api" };
```

From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

**Common mistakes.**

- **Leaving out `FallbackRecoveryTime` with the default behaviour.** Validation fails. See the next entry for why the library cannot guess it.
- **A per-request value as `PolicyName`** (a user name, a path with ids). Each value becomes a new metric time series.
- **`TagMetricsByPartitionKey = true` for IP addresses or user ids.** The same problem, per key.
- **Setting `MaxAddedRetryDelay` to zero to get "exact" Retry-After values.** Then every rejected client is told the same time and they all return together, which is the load spike the spread exists to prevent.

**See also.** [`StoreHealthOptions`](#storehealthoptions), [04-configuration.md](04-configuration.md).

<a id="fallbackrecoverytime"></a>
### `ResilientRateLimiterOptions.FallbackRecoveryTime`

**What it is.** `public TimeSpan FallbackRecoveryTime { get; init; }`. The time your fallback limiter needs to go from fully used to fully available again, with no traffic. See [fallback recovery time](01-concepts.md#fallback-recovery-time).

**What it drives.**

- **Recovery mode.** When the store answers again after an outage, the limiter stays in [recovery mode](01-concepts.md#recovery-mode) for this long, and asks the local counter first. This stops clients from getting a second allowance right after the outage (see [Recovery after an outage](01-concepts.md#recovery-after-an-outage)).
- **Warm retention.** An idle partition keeps warm state for this long, or for `MaxWarmRetention` if that is shorter.
- **The degraded Retry-After estimate.** When the store has failed and the limiter that answered gives no Retry-After value of its own (for example with `FailClosed`, where no limiter answers at all, or with a fallback that adds none), the library uses this value as its estimate. If it is zero (allowed with `FailOpen` and `FailClosed`), the estimate uses `StoreHealthOptions.BreakDuration` instead. A built-in `FixedWindowRateLimiter` fallback does give its own value (the time to its next window), and then that value is used. A `Recovery` refusal is different: the local fallback limiter is what answers it, so it carries that limiter's own value the same way a `LocalFallback` answer does.

**Why you must type it: the library cannot read it from the limiter.** It would be simpler if the library asked the fallback limiter. It cannot, because the .NET limiters do not make the needed settings public:

| Fallback limiter | Public information | Can the library compute recovery time? |
|---|---|---|
| `FixedWindowRateLimiter` (1 min) | `ReplenishmentPeriod` = 1 min | yes — the period equals the window |
| `SlidingWindowRateLimiter` (1 min, 6 segments) | `ReplenishmentPeriod` = 10 s | no — that is one [segment](01-concepts.md#segment); the number of segments is not public |
| `TokenBucketRateLimiter` (100 tokens, 10/s) | `ReplenishmentPeriod` = 1 s | no — `TokenLimit` and `TokensPerPeriod` are not public |
| custom or wrapped limiter | `IdleDuration` only | no |

`GetStatistics()` does not help either: it returns live counters (available permits, queue length, totals), not configuration. The library could read the value for a fixed window only. One rule for every limiter, "always required with `LocalFallback`", is easier to learn and harder to get wrong than "optional for one type, required for the other three".

**The right value for each kind of fallback limiter.**

| Fallback limiter | `FallbackRecoveryTime` |
|---|---|
| Fixed window | the window |
| Sliding window | the whole window, not one segment |
| Token bucket | `TokenLimit ÷ TokensPerPeriod`, rounded up, × `ReplenishmentPeriod` |
| Custom limiter | the time from fully used to fully available, with no traffic |
| Concurrency limiter | not allowed as a fallback |

Token bucket:

<!-- snippet: token-bucket-fallback -->
```csharp
var tokenLimit = 34;
var tokensPerPeriod = 34;
var replenishmentPeriod = TimeSpan.FromMinutes(1);

// Rounded up: a bucket that is not exactly full still takes one more period to finish refilling.
var fallbackRecoveryTime = Math.Ceiling(tokenLimit / (double)tokensPerPeriod) * replenishmentPeriod;

var fallback = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
{
    TokenLimit = tokenLimit,
    TokensPerPeriod = tokensPerPeriod,
    ReplenishmentPeriod = replenishmentPeriod,
    QueueLimit = 0,
});
```

From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

Sliding window:

<!-- snippet: sliding-window-fallback -->
```csharp
var window = TimeSpan.FromMinutes(1);

var fallback = new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
{
    PermitLimit = 10,
    Window = window,
    SegmentsPerWindow = 6,
    QueueLimit = 0,
});

// The recovery time is the whole window, not one segment: a caller only fully refills once the whole window has finished and a new one has started.
var fallbackRecoveryTime = window;
```

From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

**Parameters / valid values.** A `TimeSpan`. Default zero. With `LocalFallback` it must be greater than zero. With every behaviour it must be at most [`MaxFallbackRecoveryTime`](#maxfallbackrecoverytime) (1 day), because the Retry-After estimate uses it on every behaviour, not only with local fallback.

**Throws.** Nothing on its own; [`Validate()`](#limiter-options-validate) reports a wrong value.

**Common mistakes.**

- **Too large** (for example minutes typed where seconds were meant). Clients are told to wait far too long in Retry-After, and after each outage the limiter stays in recovery mode, with the stricter per-replica limit, for that whole time.
- **Too small** (for example one sliding-window segment instead of the whole window). Recovery mode ends before the local counter has really refilled, so clients can get part of a second allowance after an outage, and warm state is released too early.
- **Copying the value from another policy** that uses a different window.

**See also.** [Recovery after an outage](01-concepts.md#recovery-after-an-outage), [04-configuration.md](04-configuration.md).

<a id="maxfallbackrecoverytime"></a>
### `ResilientRateLimiterOptions.MaxFallbackRecoveryTime`

**What it is.** `public static readonly TimeSpan MaxFallbackRecoveryTime`, equal to one day. The largest `FallbackRecoveryTime` that `Validate()` accepts.

**Why it exists.** A recovery time above one day almost always means a unit mistake: hours or days typed where minutes were meant. Such a value would tell clients to wait for days, so the library refuses it at startup instead.

**When you use it.** To check a computed value before you set it, or in your own validation. It is a fixed limit, not a setting.

**Example.** No sample region shows the bound. To check a computed value yourself before you set it, compare it: `if (fallbackRecoveryTime > ResilientRateLimiterOptions.MaxFallbackRecoveryTime) { /* fix the unit */ }`. Otherwise `Validate()` reports it for you.

**Common mistakes.** A fallback with a window longer than one day. The library refuses its recovery time. Even a one-day window, which is accepted, is a poor fit for a local fallback: a restart clears the local count, and Retry-After estimates become very long (see [Choosing a window length](01-concepts.md#choosing-a-window-length)).

**See also.** [`FallbackRecoveryTime`](#fallbackrecoverytime), [`ResilientRateLimiterOptions.Validate()`](#limiter-options-validate).

<a id="limiter-options-validate"></a>
### `ResilientRateLimiterOptions.Validate()`

**What it is.** `public void Validate()`. It checks these rules and throws if any is broken:

- `MaxWarmRetention` is greater than zero;
- `FallbackRecoveryTime` is greater than zero when `FailureBehavior` is `LocalFallback`;
- `FallbackRecoveryTime` is at most one day, whatever the `FailureBehavior`;
- `MaxAddedRetryDelay` is not negative.

The constructor, `WithResilience`, and (at the first request for each key) `ResilientRateLimitPartition.Get` call it for you.

**When you use it.** At startup, for options you pass to `ResilientRateLimitPartition.Get`, so that a mistake stops the app before the first request instead of at it.

**Returns.** Nothing, when the options are valid.

**Throws.** `InvalidOperationException` when at least one rule is broken. The message lists **every** broken rule, one per line.

**Example.** Here `MaxWarmRetention` is zero and `FallbackRecoveryTime` is missing:

<!-- snippet: validate -->
```csharp
var invalid = new ResilientRateLimiterOptions { MaxWarmRetention = TimeSpan.Zero };

try
{
    invalid.Validate();
}
catch (InvalidOperationException exception)
{
    Console.WriteLine(exception.Message);
}
```

From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

It prints:

```text
MaxWarmRetention must be greater than zero.
FallbackRecoveryTime must be set when FailureBehavior is LocalFallback. A RateLimiter cannot be asked how long it takes to refill.
```

**Common mistakes.** Relying on `ResilientRateLimitPartition.Get` to validate. It does, but only when a key is first used, so a wrong setup passes startup and fails on live traffic.

<a id="storefailurebehavior"></a>
### `StoreFailureBehavior` (enum)

**What it is.** `public enum StoreFailureBehavior`. The path that answers when the store cannot: it is slow, it is down, or the breaker is open. You set it on `ResilientRateLimiterOptions.FailureBehavior`. See [StoreFailureBehavior](01-concepts.md#storefailurebehavior) and [When the shared store is slow or down](01-concepts.md#when-the-shared-store-is-slow-or-down).

**When you use it.** Once per policy, when you decide which risk is worse for that endpoint: too much traffic, or refused customers.

**Values.**

| Value | What happens when the store fails | Source tag | Fallback limiter | Recovery mode | What a caller should do |
|---|---|---|---|---|---|
| `LocalFallback` (default) | The local fallback limiter answers, with a per-replica share of the limit. | `LocalFallback` | required | yes | Treat the answer as normal. Alert if it lasts long. |
| `FailOpen` | Every request is allowed. Rate limiting stops while the store is down. | `FailOpen` | not needed | no | Nothing; accept that limits do not apply for now. |
| `FailClosed` | Every request is rejected, with an estimated Retry-After. | `FailClosed` | not needed | no | Retry after the Retry-After time. |

**`LocalFallback`.** The safe middle: limits still apply, but per replica, sized with [`LocalBudget.ForReplicas`](01-concepts.md#local-budget). Without it, you must pick between the two extremes below. Needs a fallback limiter and `FallbackRecoveryTime`.

<!-- snippet: limiter-options-all -->
```csharp
var options = new ResilientRateLimiterOptions
{
    FailureBehavior = StoreFailureBehavior.LocalFallback,
    FallbackRecoveryTime = TimeSpan.FromSeconds(10),
    MaxWarmRetention = TimeSpan.FromMinutes(2),
    PolicyName = "orders-api",
    TagMetricsByPartitionKey = false,
    MaxAddedRetryDelay = TimeSpan.FromSeconds(60),
};
```

From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

**`FailOpen`.** For limits that are a courtesy, where refusing a real customer is worse than letting too much through. The risk: during an outage there is no protection at all, so a client that misbehaves at that moment reaches your service unlimited.

<!-- snippet: fail-open -->
```csharp
var options = new ResilientRateLimiterOptions { FailureBehavior = StoreFailureBehavior.FailOpen };

using var limiter = primary.WithResilience(fallback: null, options, storeHealth);
```

From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

**`FailClosed`.** For limits that protect something that must never be overrun, such as a paid quota, where refusing is safer than overshooting. The risk: a store outage becomes an outage of this endpoint. The Retry-After estimate is `FallbackRecoveryTime` if you set it, otherwise `StoreHealthOptions.BreakDuration`, plus the added delay from `MaxAddedRetryDelay`.

<!-- snippet: fail-closed -->
```csharp
var options = new ResilientRateLimiterOptions { FailureBehavior = StoreFailureBehavior.FailClosed };

using var limiter = primary.WithResilience(fallback: null, options, storeHealth);
```

From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

**Common mistakes.**

- **`FailOpen` or `FailClosed` with a fallback limiter passed.** The fallback is never asked; it is only disposed. Pass `fallback: null` so the code says what it does.
- **`FailOpen` on an endpoint that protects a fragile downstream system.** The store outage and a traffic spike often come together.
- **Expecting recovery mode with `FailOpen` or `FailClosed`.** It exists only with `LocalFallback`, because only then is there a local count to check.

**See also.** [`ResilientRateLimiterOptions`](#limiter-options), [`AcquireAsync`](#acquireasync).

<a id="sizing"></a>
## Sizing

**The problem.** During an outage each [replica](01-concepts.md#replica) limits on its own, with its local fallback limiter. The shared limit (for example 100 requests per minute for all replicas together) must be split between them.

**What goes wrong without a rule.** If every replica's fallback allows the full shared limit, three replicas together allow three times the limit during an outage. If you type a fixed share by hand, it is right on the day you write it and wrong after the next change to the replica count.

**What the library does.** It gives you one small method that computes the share. It cannot apply it for you, because you build the fallback limiter yourself.

<a id="localbudget-forreplicas"></a>
### `LocalBudget.ForReplicas(sharedPermitLimit, replicaCount)`

**What it is.** `public static int ForReplicas(int sharedPermitLimit, int replicaCount)`. It returns the [local budget](01-concepts.md#local-budget): the shared limit divided by the number of replicas, rounded up to a whole number. It is arithmetic only.

**When you use it.** Every time you build a fallback limiter, to set its `PermitLimit` (or `TokenLimit` for a token bucket).

**Parameters.**

| Name | Type | Required | Default | Meaning | Valid values |
|---|---|---|---|---|---|
| `sharedPermitLimit` | `int` | yes | — | The limit enforced across every replica together. | 1 or more. |
| `replicaCount` | `int` | yes | — | The number of replicas you **typically** run. | 1 or more. |

**Returns.** `sharedPermitLimit ÷ replicaCount`, rounded up. For 100 and 3 it returns 34.

**Rounding up.** 100 ÷ 3 is 33.33. Rounding down would give 33, and three replicas would then allow only 99 in total, less than the limit you promised. Rounding up gives 34, so the total is 102: at most one permit per replica above the limit, never below it.

**Typical, not maximum, replica count.** Suppose you usually run 3 replicas and can scale to 10. With 10, each replica gets 10 permits, and during an outage your 3 replicas allow only 30 of the 100 you promised: good clients are refused. With 3, each replica gets 34. If you run 10 replicas at the moment of an outage, the total can reach 340 for as long as the outage lasts. That overshoot is the price of not refusing good traffic in the normal case; choose the typical count unless overshoot is worse for you than refusals.

**Throws.**

| Exception | Exact cause |
|---|---|
| `ArgumentOutOfRangeException` | `sharedPermitLimit` or `replicaCount` is below 1. |

**Example.**

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

(This fallback is disposed by its own `using` because the sample never hands it to a wrapper. A fallback you pass to a wrapper must not have a `using`.)

**Common mistakes.**

- **A literal number instead of this method.** The fallback slowly drifts out of proportion as replicas are added or removed, and nothing tells you.
- **A replica count of 0 from configuration** that was never set: the method throws at startup, which is better than a fallback of unlimited size.
- **Using the result for the store limiter.** The store limiter keeps the full shared limit; only the fallback gets the share.

**See also.** [The local budget](01-concepts.md#the-local-budget), [04-configuration.md](04-configuration.md).

<a id="reading-results"></a>
## Reading results

**The problem.** A rejected request looks the same whether the shared store rejected it or a local fallback did during an outage. An allowed request looks the same whether the store counted it or the library let it through because the store was down.

**What goes wrong without this information.** You cannot tell from logs, metrics or a response whether your service is running on its real limits, and you cannot give a caller a useful time to retry.

**What the library does.** Every lease from `AcquireAsync` is a `ResilientRateLimitLease`. It carries a [source tag](01-concepts.md#source-tag) (a `LeaseSource` value) and, on a rejection, usually a [Retry-After](01-concepts.md#retry-after-header) value, both as lease [metadata](01-concepts.md#metadata): named extra values a lease can carry.

<a id="resilientratelimitlease"></a>
### `ResilientRateLimitLease` (class)

**What it is.** `public sealed class ResilientRateLimitLease : RateLimitLease`. A decorator around the lease that the answering limiter produced (the inner lease). It adds the source tag, can replace the Retry-After value, and passes everything else through: `IsAcquired` and every other metadata name come from the inner lease.

**When you use it.** You read it on every lease you get from a `ResilientRateLimiter`. You create one yourself only when you write your own decorator around a limiter, or a test double.

**Members.**

| Member | What it does |
|---|---|
| `IsAcquired` | The inner lease's answer. |
| `Source` | The `LeaseSource` of this lease (see below). Use it when you hold the concrete type. |
| `SourceMetadata` | `public static readonly MetadataName<LeaseSource>`, named `"ResilientRateLimiting.Source"`. The metadata key for the source; use it when you hold a plain `RateLimitLease`, as the ASP.NET Core middleware does. |
| `MetadataNames` | The inner lease's names, plus `"ResilientRateLimiting.Source"`, plus the standard `MetadataName.RetryAfter` name when this lease has its own Retry-After value that the inner lease does not already list. |
| `TryGetMetadata(name, out value)` | Answers the source and this lease's own Retry-After itself; passes every other name to the inner lease. |
| `Dispose()` | Disposes the inner lease. |

**Where the Retry-After value comes from.** On a rejection, the library first uses the answering limiter's own `MetadataName.RetryAfter` value, and adds a [random spread](01-concepts.md#random-spread) (and, while the store is failing, a longer wait), capped by `MaxAddedRetryDelay`. Only when the answering limiter gives no value, and the store has failed, does it estimate one from `FallbackRecoveryTime` (or `BreakDuration`). The result replaces the inner lease's own value. Two cases give **no** Retry-After:

- an allowed lease;
- a rejection by a healthy store that gave no value. `RedisRateLimiting` limiters give no value under the standard name (**measured**, see [measurements.md](measurements.md#redisratelimiting-and-the-retry-after-value)), so a normal rejection by Redis carries no Retry-After.

<a id="lease-constructor"></a>
### `new ResilientRateLimitLease(inner, source, retryAfter = null)`

**What it is.** `public ResilientRateLimitLease(RateLimitLease inner, LeaseSource source, TimeSpan? retryAfter = null)`.

**When you use it.** In your own decorator or test code, to produce a lease that looks exactly like one from the library, so that code reading the source tag (for example `UseResilientDefaults`) can be tested.

**Parameters.**

| Name | Type | Required | Default | Meaning | Valid values |
|---|---|---|---|---|---|
| `inner` | `RateLimitLease` | yes | — | The lease the answering limiter produced. The new lease owns it and disposes it. | Not `null`. |
| `source` | `LeaseSource` | yes | — | Which path answered. | Any `LeaseSource` value. |
| `retryAfter` | `TimeSpan?` | no | `null` | When given, replaces the inner lease's own Retry-After value. When `null`, the inner value (if any) is passed through. | Any value, or `null`. |

**Throws.** `ArgumentNullException` when `inner` is `null`.

**Example.** The inner fixed-window lease has its own Retry-After (the time to its next window); the 30 seconds given here replace it:

<!-- snippet: custom-lease -->
```csharp
using var innerLimiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
{
    PermitLimit = 1,
    Window = TimeSpan.FromMinutes(1),
    QueueLimit = 0,
});

using var first = innerLimiter.AttemptAcquire(1);
using var rejected = new ResilientRateLimitLease(innerLimiter.AttemptAcquire(1), LeaseSource.LocalFallback, retryAfter: TimeSpan.FromSeconds(30));

rejected.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter);
Console.WriteLine($"Source: {rejected.Source}, allowed: {rejected.IsAcquired}, retry after: {retryAfter}");
```

From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

It prints `Source: LocalFallback, allowed: False, retry after: 00:00:30`.

Reading the Retry-After of a real lease:

<!-- snippet: read-retry-after -->
```csharp
var hasRetryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter);

Console.WriteLine(hasRetryAfter ? $"Retry after: {retryAfter}" : "No Retry-After hint.");
```

From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

**Common mistakes.**

- **Disposing the inner lease yourself** as well as the wrapper lease. The wrapper lease already disposes it.
- **Expecting a Retry-After on every rejection.** A healthy Redis limiter gives none; check the return value of `TryGetMetadata`.
- **Wrapping a lease that is already a `ResilientRateLimitLease`.** The outer source tag hides the inner one, the same problem as nesting wrappers.

**See also.** [`ResilientRateLimitLease`](#resilientratelimitlease), [`LeaseSource`](#leasesource), [`UseResilientDefaults`](#useresilientdefaults).

<a id="leasesource"></a>
### `LeaseSource` (enum)

**What it is.** `public enum LeaseSource`. Which path produced a lease. Read it with `lease.TryGetMetadata(ResilientRateLimitLease.SourceMetadata, out var source)`, or from `ResilientRateLimitLease.Source`. The same value is the `source` tag on the lease metric (in snake case, for example `local_fallback`; see [05-telemetry.md](05-telemetry.md)).

**Values.**

| Value | When it happens | Allowed or rejected | What a caller should do |
|---|---|---|---|
| `Distributed` | The shared store answered in time. The normal case. | either | Nothing special. A rejection is a real limit; Retry-After is present only if the store gave one. |
| `LocalFallback` | The store failed (error, timeout or open breaker), `FailureBehavior` is `LocalFallback`, and the local fallback limiter answered. | either | Treat it as a normal answer. Many of these in a row mean an outage: alert on the metric. |
| `FailOpen` | The store failed and `FailureBehavior` is `FailOpen`. | always allowed | Nothing. Know that no limit applied to this request. |
| `FailClosed` | The store failed and `FailureBehavior` is `FailClosed`. | always rejected | Retry after the Retry-After time. The request was refused because the store is down, not because the client used too much. |
| `Recovery` | The store answered again after an outage a short time ago ([recovery mode](01-concepts.md#recovery-mode)), and the local counter refused the request before the store was asked. Only with `LocalFallback`. | always rejected | Retry after the Retry-After time. The client used its share during the outage. |

A lease from `AttemptAcquire` has no source at all (see [`AttemptAcquire`](#attemptacquire)).

**Example.** The [`AcquireAsync`](#acquireasync) entry shows a `switch` over every value (`read-source`).

**Common mistakes.**

- **Reading a missing source as `Distributed`.** `TryGetMetadata` returns `false` for a lease with no source; `out var source` is then the default value of the enum, which is `Distributed`. Check the return value.
- **Alerting on single `LocalFallback` leases.** One slow store call gives one; alert on a rate over time.

**See also.** [`ResilientRateLimitLease`](#resilientratelimitlease), [`StoreFailureBehavior`](#storefailurebehavior), [`AcquireAsync`](#acquireasync), [05-telemetry.md](05-telemetry.md).

<a id="aspnet-core"></a>
## ASP.NET Core

**The problem.** ASP.NET Core has its own rate limiting middleware (`AddRateLimiter` and `app.UseRateLimiter()`). By default it answers a rejected request with status 503 (Service Unavailable) and writes no `Retry-After` header. It also knows nothing about one shared `StoreHealth`.

**What goes wrong without help.** Clients read 503 as "the server is broken", not "you sent too much", and retry at once, because nothing told them when to come back. Each policy may create its own `StoreHealth`, which breaks the breaker (see [`StoreHealth`](#storehealth)).

**What the library does.** The `ResilientRateLimiting.AspNetCore` package adds three extension methods: one to answer rejections correctly, one to register the single `StoreHealth` with configuration and logging, and one to add logging to a hand-built `StoreHealthOptions`.

<a id="useresilientdefaults"></a>
### `RateLimiterOptions.UseResilientDefaults(emitDegradedHeader = null)`

**What it is.** `public static RateLimiterOptions UseResilientDefaults(this RateLimiterOptions options, Func<HttpContext, bool>? emitDegradedHeader = null)`. It sets two things on the middleware options:

- `RejectionStatusCode` to **429** (Too Many Requests);
- `OnRejected` to a callback that writes the response headers below.

It does not touch any policy.

**Headers written on a rejection.**

| Header | When | Format |
|---|---|---|
| `Retry-After` | The lease has a `MetadataName.RetryAfter` value. | Whole seconds, rounded up (a 1.2-second wait becomes `2`), never negative, at most `int.MaxValue`, written with invariant digits. |
| `X-RateLimit-Degraded: true` | `emitDegradedHeader` is given, the lease has a source other than `Distributed`, and the predicate returns `true` for this request. | The literal value `true`. |

**When you use it.** Once, inside `AddRateLimiter`, in every web app that uses this library.

**Parameters.**

| Name | Type | Required | Default | Meaning | Valid values |
|---|---|---|---|---|---|
| `options` | `RateLimiterOptions` | yes | — | The middleware options (the `this` argument). | Not `null`. |
| `emitDegradedHeader` | `Func<HttpContext, bool>?` | no | `null` | Decides per rejected request whether to tell this caller that limiting runs on local state. Asked only for rejected requests whose lease is not `Distributed`. | Any predicate, or `null`. |

**Returns.** The same `options`, so you can chain calls. **Throws.** `ArgumentNullException` when `options` is `null`.

**Variation: `emitDegradedHeader` left out (`null`).** Write `limiterOptions.UseResilientDefaults();`. You get 429 and `Retry-After`, and the degraded header is never sent. This is the right choice for public clients: whether your store is down is internal information.

**Variation: with a predicate.** Send the degraded header only to callers you trust, here requests from the same machine:

<!-- snippet: web-degraded-header -->
```csharp
limiterOptions.UseResilientDefaults(emitDegradedHeader: context =>
    context.Connection.RemoteIpAddress is { } remoteIp && IPAddress.IsLoopback(remoteIp));
```

From `samples/ResilientRateLimiting.Samples.Web/Program.cs`

**Common mistakes.**

- **Setting your own `OnRejected` after this call, or calling this twice.** There is only one `OnRejected`; the last assignment wins and the earlier callback is lost without a message. If you need extra work on rejection, set `OnRejected` yourself and write the headers there.
- **Setting `OnRejected` before this call.** This call replaces it.
- **Expecting a `Retry-After` header for every 429.** A rejection by a healthy Redis limiter carries no value, so no header (see [`ResilientRateLimitLease`](#resilientratelimitlease)).
- **A predicate that returns `true` for everyone.** It tells every client, including attackers, when your store is down.

**See also.** [Retry-After](01-concepts.md#retry-after), [`LeaseSource`](#leasesource).

<a id="addresilientratelimiting"></a>
### `IServiceCollection.AddResilientRateLimiting(storeHealthSection, configure = null)`

**What it is.** `public static IServiceCollection AddResilientRateLimiting(this IServiceCollection services, IConfiguration storeHealthSection, Func<StoreHealthOptions, StoreHealthOptions>? configure = null)`. It registers one singleton `StoreHealth` for your store connection, in four steps:

1. binds `StoreHealthOptions` from `storeHealthSection` (the `appsettings.json` form is in [04-configuration.md](04-configuration.md));
2. runs `configure` on the bound options, if given;
3. adds logging with [`WithLogging`](#withlogging), under the logger category `"ResilientRateLimiting.StoreHealth"`;
4. builds the `StoreHealth` with the `TimeProvider` registered in dependency injection, or `TimeProvider.System` if none is registered.

Steps 2 to 4 run when the `StoreHealth` is first resolved, not when this method is called.

**When you use it.** Once per application, for the store connection your policies share. In a policy, get the instance with `context.RequestServices.GetRequiredService<StoreHealth>()`. It registers only the `StoreHealth`; build your `ResilientRateLimiterOptions` once at startup and capture them in each policy.

**Parameters.**

| Name | Type | Required | Default | Meaning | Valid values |
|---|---|---|---|---|---|
| `services` | `IServiceCollection` | yes | — | The service collection (the `this` argument). | Not `null`; this method not called on it before. |
| `storeHealthSection` | `IConfiguration` | yes | — | The configuration section for this store connection. | Not `null`. Missing keys keep their defaults. |
| `configure` | `Func<StoreHealthOptions, StoreHealthOptions>?` | no | `null` | Changes the bound options, for the settings configuration cannot hold: `ShouldHandle` and `OnStoreFailure`. Return a copy made with `with`. | Any function, or `null`. |

**Returns.** The same `services`, for chaining.

**Throws.**

| Exception | When | Exact cause |
|---|---|---|
| `ArgumentNullException` | at the call | `services` or `storeHealthSection` is `null`. |
| `InvalidOperationException` | at the call | This method was already called on the same `services` (a `StoreHealth` is already registered). |
| `OptionsValidationException` | at application startup | The values bound from configuration fail `StoreHealthOptions.Validate()`. The message holds the rule lines. |
| `InvalidOperationException` | at first resolution of `StoreHealth` (normally the first request that uses a policy) | The options returned by `configure` fail `Validate()`. Startup validation only sees the bound values, not the result of `configure`. |

**Variation: `configure` left out.** Write `builder.Services.AddResilientRateLimiting(builder.Configuration.GetSection("ResilientRateLimiting:Store"));`. The options come only from configuration, with logging added. With `ShouldHandle` left `null`, the default classification applies (see [`StoreHealthOptions`](#storehealthoptions)).

**Variation: `configure` given.** Here it names the store's own exceptions and adds a callback:

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

<!-- snippet: web-services -->
```csharp
builder.Services.AddSingleton(redis);
builder.Services.AddResilientRateLimiting(
    builder.Configuration.GetSection("ResilientRateLimiting:Store"),
    Configure);
```

From `samples/ResilientRateLimiting.Samples.Web/Program.cs`

Your `OnStoreFailure` still runs: logging is added after `configure`, and `WithLogging` keeps an existing callback.

**Common mistakes.**

- **Calling it twice for two store connections.** The second call throws. Build the second connection's `StoreHealth` by hand (see [`WithLogging`](#withlogging)).
- **Registering the hand-built second `StoreHealth` as another unkeyed service.** Dependency injection returns the last registration, so every policy, including the first store's, would get the second store's health. Capture it in the one policy that needs it.
- **Making the options invalid in `configure`.** Startup does not catch it; the first request that uses a policy does. Test `configure` or call `Validate()` on its result.
- **Calling `WithLogging` inside `configure`.** Logging is added for you; you would log each failure twice.

**See also.** [`StoreHealth`](#storehealth), [04-configuration.md](04-configuration.md), [05-telemetry.md](05-telemetry.md).

<a id="withlogging"></a>
### `StoreHealthOptions.WithLogging(logger)`

**What it is.** `public static StoreHealthOptions WithLogging(this StoreHealthOptions options, ILogger logger)`. It returns a **copy** of the options whose `OnStoreFailure` writes a log entry at `Warning` level and then calls the callback that was already set, if any. The original options do not change.

The message is `Store call failed: {ExceptionType}`, with only the exception's type name in the text. The exception itself is passed to the logger too, so a provider that shows exception details will show them. Entries follow the `OnStoreFailure` repeat rules: the first failure of each exception type, and again after that type was quiet for `BreakerSamplingDuration` or after the breaker closed.

**When you use it.** For a `StoreHealth` you build by hand, typically for a second store connection. `AddResilientRateLimiting` already calls it for its own connection.

**Parameters.**

| Name | Type | Required | Default | Meaning | Valid values |
|---|---|---|---|---|---|
| `options` | `StoreHealthOptions` | yes | — | The options to copy (the `this` argument). | Not `null`. |
| `logger` | `ILogger` | yes | — | Receives the warnings. | Not `null`. |

**Returns.** A new `StoreHealthOptions` with the combined callback.

**Throws.** `ArgumentNullException` when `options` or `logger` is `null`.

**Example.** A second store connection with its own hand-built `StoreHealth`:

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

(`AsyncTimeout = 20` is the Redis client's timeout in milliseconds, kept at or below the 20 ms `StoreTimeout` default, a **starting point**; see [06-production.md](06-production.md).)

**Common mistakes.**

- **Calling it twice on the same options.** The callback is wrapped twice, so every failure is logged twice.
- **Ignoring the return value.** `options.WithLogging(logger);` alone changes nothing; pass the returned copy to `StoreHealth`.

**See also.** [`AddResilientRateLimiting`](#addresilientratelimiting), [`StoreHealthOptions`](#storehealthoptions).

## Exceptions you may see

"Reaches the caller" means the exception comes out of your call; the library does not turn it into a fallback answer.

| Exception | Thrown by | Cause | Fix |
|---|---|---|---|
| `ArgumentOutOfRangeException` | `AcquireAsync(n)`, from the primary limiter | `n` is above the store limiter's own limit. `RedisRateLimiting` limiters throw this instead of returning a rejected lease. It is an `ArgumentException`, so it reaches the caller and does not start the fallback. | Check `n` against the limit before you call (see [`AcquireAsync`](#acquireasync)). Do **not** make `ShouldHandle` treat it as a store failure: `ShouldHandle` also feeds the breaker, so one client could push every request on the connection to the fallback. |
| `ArgumentOutOfRangeException` | `AcquireAsync(n)`, from the .NET `RateLimiter` base class | `n` is negative. The store is never asked. | Pass 0 or more. |
| `ArgumentOutOfRangeException` | `LocalBudget.ForReplicas` | `sharedPermitLimit` or `replicaCount` is below 1. | Check the configuration values it reads, often a replica count that was never set. |
| `ArgumentNullException` | `ResilientRateLimiter` constructor, `WithResilience`, `ResilientRateLimitPartition.Get`, `StoreHealth` constructor, `ResilientRateLimitLease` constructor, `UseResilientDefaults`, `AddResilientRateLimiting`, `WithLogging` | A required argument is `null`; or no fallback is given while `FailureBehavior` is `LocalFallback` (for `Get`: at the first request for a key). | Pass the argument; or set `FailureBehavior` to `FailOpen` or `FailClosed` if you really want no fallback. |
| `InvalidOperationException` | `ResilientRateLimiterOptions.Validate()`, `StoreHealthOptions.Validate()`, and every constructor or method that calls them | The options are incomplete or contradictory. The message lists every broken rule, one per line. | Fix each listed setting. See [`ResilientRateLimiterOptions.Validate()`](#limiter-options-validate) and [`StoreHealthOptions.Validate()`](#storehealthoptions-validate). |
| `InvalidOperationException` | `AddResilientRateLimiting` | It was called a second time on the same service collection. | Call it once. Build a second connection's `StoreHealth` by hand with [`WithLogging`](#withlogging). |
| `InvalidOperationException` | first resolution of the registered `StoreHealth` | The options returned by `configure` fail validation. | Fix `configure`. |
| `Microsoft.Extensions.Options.OptionsValidationException` | application startup (`ValidateOnStart` from `AddResilientRateLimiting`) | The `StoreHealthOptions` values bound from configuration fail validation. | Fix `appsettings.json` or the other configuration source. |
| `OperationCanceledException` | `AcquireAsync` | Your cancellation token was cancelled. Never treated as a store failure. | Expected when a request is aborted; nothing to fix. |
| `ObjectDisposedException` | `AcquireAsync` | The primary or fallback limiter was disposed, often by a `using` on a limiter you handed to the wrapper or to a partition factory. | Remove that `using`; dispose only the wrapper. |
| `StackExchange.Redis.RedisConnectionException` | a `RedisRateLimiting` limiter used **without** this library | Redis cannot be reached. Each call waits about 5 seconds and then throws (**measured**, see [measurements.md](measurements.md#redisratelimiting-alone-while-redis-is-unreachable)). | Wrap the limiter with this library. Wrapped, the exception is a store failure: the fallback path answers within `StoreTimeout` and the breaker stops further calls. |

With `ShouldHandle` set, your predicate decides for every exception from the store limiter except caller cancellation and the library's own timeout and open-breaker exceptions (see [`StoreHealthOptions`](#storehealthoptions)). Exceptions thrown by the fallback limiter always reach the caller.

Next: [04-configuration.md](04-configuration.md) — every option in detail: why each default, when to change it, and what goes wrong if it is wrong.
