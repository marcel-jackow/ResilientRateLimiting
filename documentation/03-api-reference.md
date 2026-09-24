# API reference

This page explains every public type and member of the library, and each of its variations: overloads, optional parameters given or left out, and enum values. Each entry has the same parts: what it is, when you use it, its parameters, what it returns and throws, an example, common mistakes, and links to related entries.

You do not need to know rate limiting to read it, but it moves fast. If a word is new, follow its link to the [glossary](01-concepts.md#glossary) in [01-concepts.md](01-concepts.md). If you have never used the library, start with [02-getting-started.md](02-getting-started.md).

A note on numbers: every default value on this page is a **starting point**, not a measured best value, unless it says **measured** and links to [measurements.md](measurements.md). [04-configuration.md](04-configuration.md) explains why each default was chosen and when to change it.

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

**Common mistakes.** Code that reads `GetStatistics()!.CurrentAvailablePermits` gets a `NullReferenceException`.

<a id="idleduration"></a>
### `IdleDuration`

**What it is.** A property from `RateLimiter`, `TimeSpan? IdleDuration`. `PartitionedRateLimiter` reads it to find [partitions](01-concepts.md#partition) that nobody uses, and removes their limiters to free memory. `null` means "not idle, keep me".

The wrapper returns:

- `null` while a request is in progress;
- `null` while the fallback limiter holds [warm fallback state](01-concepts.md#warm-fallback-state), unless the number of live partitions on the store connection is above `StoreHealthOptions.MaxWarmPartitions`;
- otherwise, the time since this limiter last served a request (or since it was created, if it has served none).

Warm state is held for at most `MaxWarmRetention` or `FallbackRecoveryTime`, whichever is shorter (see [Warm state](01-concepts.md#warm-state)).

**When you use it.** You do not read it yourself. It is how the wrapper keeps a partition alive just long enough.

**Common mistakes.** Wrapping a primary limiter whose partitions you expect to be removed at once. With `LocalFallback`, an idle partition stays in memory for up to the warm retention time. That is intended; the cost per partition is about 1 KB (**measured**, see [measurements.md](measurements.md#memory-per-partition)).

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
- **The degraded Retry-After estimate.** When the store has failed and the limiter that answered gives no Retry-After value of its own (for example with `FailClosed`, for a recovery-gate refusal, or with a fallback that adds none), the library uses this value as its estimate. If it is zero (allowed with `FailOpen` and `FailClosed`), the estimate uses `StoreHealthOptions.BreakDuration` instead. A built-in `FixedWindowRateLimiter` fallback does give its own value (the time to its next window), and then that value is used.

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

// The recovery time is the whole window, not one segment: a caller only fully refills once the whole window has rolled over.
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

**Common mistakes.** A fallback with a window longer than one day. The library refuses its recovery time. Even a one-day window, which is accepted, is a poor fit for a local fallback: a restart clears the local count, and Retry-After estimates become very long (see [Choosing a window length](01-concepts.md#choosing-a-window-length)).

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

Next: [04-configuration.md](04-configuration.md) — every option in detail: why each default, when to change it, and what goes wrong if it is wrong.
