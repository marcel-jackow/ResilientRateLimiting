# Concepts

This page teaches the ideas behind the library from zero: what a rate limiter is, why many copies of a service need a shared counter, and what can go wrong when that shared counter is slow or down. Read it before the other pages. Every term in **bold** on first use has an entry in the [Glossary](#glossary) at the end.

## Who this is for

This page is for a .NET developer who has never used `System.Threading.RateLimiting`, Redis or a rate limiter before. If you already know these topics, read [Which limiter should I use?](#which-limiter-should-i-use) and [When the shared store is slow or down](#when-the-shared-store-is-slow-or-down), then go to [02-getting-started.md](02-getting-started.md).

## What a rate limiter does

**The problem.** A web service can answer only so many **requests** in a given time. It has a limited number of CPU cores, database connections and memory. If one client (or a bug in one client, or an attacker) sends far more requests than usual, the service slows down for everybody. In the worst case it stops answering at all.

**What goes wrong without a limit.** Nothing stops the extra traffic. The busy client uses all the capacity, and other clients get slow answers or errors. The service is overloaded even though most clients behave well.

**What a rate limiter does.** A [rate limiter](#rate-limiter) sets a **limit**, for example "at most 100 requests per minute" (an example number, not a recommendation). Before your code handles a request, it asks the limiter: "may I handle this one?" The limiter counts, and says yes while the count is under the limit and no after that. Rejected requests are cheap: your code does not do the real work for them. So the rate limiter protects the service from overload.

Three words appear everywhere in rate limiting:

- A [permit](#permit) is one unit of the limit. Usually one request costs one permit. A limit of 100 permits per minute means 100 requests per minute.
- To ask the limiter, you call [`AcquireAsync`](#acquireasync) with the number of permits you need.
- The limiter answers with a [lease](#lease). A lease is the answer object: its `IsAcquired` property says "allowed" (`true`) or "rejected" (`false`). A lease can also carry [metadata](#metadata): extra facts about the answer, for example how long the caller should wait before it tries again (the [Retry-After](#retry-after-header) value). You dispose the lease when the request is finished.

.NET has this model built in. The `System.Threading.RateLimiting` namespace defines the base class `RateLimiter`, the `RateLimitLease` class, and ready limiters that count in memory. ASP.NET Core uses the same types in its rate limiting middleware (`AddRateLimiter` and `UseRateLimiter`). This library also uses the same types, so everything on this page applies to it.

Here is a complete example with a built-in limiter. The limit is 2 requests per minute, and the code sends 3 requests:

<!-- snippet: built-in-limiter -->
```csharp
using var limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
{
    PermitLimit = 2,
    Window = TimeSpan.FromMinutes(1),
    QueueLimit = 0,
});

for (var request = 1; request <= 3; request++)
{
    using var lease = await limiter.AcquireAsync(permitCount: 1);

    if (lease.IsAcquired)
    {
        Console.WriteLine($"Request {request}: allowed");
    }
    else
    {
        lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter);
        Console.WriteLine($"Request {request}: rejected, retry after {retryAfter}");
    }
}
```
From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

When you run the console sample, it prints:

```text
Request 1: allowed
Request 2: allowed
Request 3: rejected, retry after 00:01:00
```

`QueueLimit = 0` means "do not wait". A limiter can also hold a request in a [queue](#queue) until a permit is free. With a queue limit of zero, a request over the limit is rejected at once. Most services that protect themselves from overload want this: a waiting request still holds a connection and memory.

## Kinds of limiter

A limiter must decide *when* used permits come back. There are four common answers, and `System.Threading.RateLimiting` has one class for each.

- **[Fixed window](#fixed-window)** (`FixedWindowRateLimiter`). Time is cut into equal blocks, for example one minute each. Each block allows up to the limit. When a new block starts, the count goes back to zero. Example: "100 requests per minute, the count resets at the start of each minute." It is simple and uses little memory. Its weak point: a client can send 100 requests at the end of one minute and 100 more at the start of the next, so 200 requests arrive within a few seconds.
- **[Sliding window](#sliding-window)** (`SlidingWindowRateLimiter`). The window moves with time: "at most 100 requests in *any* 60 seconds". The built-in class splits the window into [segments](#segment) (for example 6 segments of 10 seconds). When a segment grows older than the window, its permits come back. This removes most of the burst at the window edge, and it costs a little more memory.
- **[Token bucket](#token-bucket)** (`TokenBucketRateLimiter`). Picture a bucket that holds up to a fixed number of tokens. Each request takes a token. At a fixed period, a fixed number of tokens is added back, never more than the bucket holds. Example: "a bucket of 20 tokens; 10 tokens are added every second." This allows a short burst (up to 20 at once) but a steady rate of 10 per second over time.
- **[Concurrency](#concurrency-limiter)** (`ConcurrencyLimiter`). This one does not count requests over time. It counts requests *running at the same time*. Example: "at most 5 requests in progress." A permit comes back as soon as its lease is disposed, that is, when the request finishes. It protects a resource that can do only a few things at once, for example a slow downstream system.

| Kind | What it counts | When permits come back |
|---|---|---|
| Fixed window | Requests in the current block of time | All at once, when the next block starts |
| Sliding window | Requests in the last full window | Segment by segment, as each segment grows older than the window |
| Token bucket | Tokens left in the bucket | A fixed number of tokens every period, up to the bucket size |
| Concurrency | Requests in progress right now | Each one when its lease is disposed |

One example of each (all numbers are examples):

<!-- snippet: limiter-kinds -->
```csharp
// At most 100 requests in each fixed minute (10:00:00-10:00:59, then 10:01:00-10:01:59, ...).
using var fixedWindow = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
{
    PermitLimit = 100,
    Window = TimeSpan.FromMinutes(1),
});

// At most 100 requests in any rolling minute, tracked in 6 segments of 10 seconds.
using var slidingWindow = new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
{
    PermitLimit = 100,
    Window = TimeSpan.FromMinutes(1),
    SegmentsPerWindow = 6,
});

// A bucket of 20 tokens; 10 tokens are added back every second.
using var tokenBucket = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
{
    TokenLimit = 20,
    TokensPerPeriod = 10,
    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
});

// At most 5 requests running at the same time; a permit comes back when its lease is disposed.
using var concurrency = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
{
    PermitLimit = 5,
});
```
From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

The difference between the first three kinds and the concurrency kind matters later in this library. A window or token bucket *remembers recent traffic*: after 100 requests it still knows about them for the rest of the window. A concurrency limiter forgets a request the moment the request ends. That is why this library's local fallback limiter must be a window or token-bucket limiter, never a `ConcurrencyLimiter` (see [When the shared store is slow or down](#when-the-shared-store-is-slow-or-down)).

## Partitions

**The problem.** One limit for the whole service is rarely what you want. If the service allows 1,000 requests per minute in total, one noisy client can use all 1,000, and every other client is rejected.

**What goes wrong without partitions.** The limit protects the service, but not the clients from each other. A well-behaved client is rejected because of somebody else's traffic.

**What partitions do.** A [partition](#partition) is a separate limiter for each [partition key](#partition-key). The key is a value you take from the request: a client ID, an API key, a tenant ID, or a user ID. "100 requests per minute *per client*" means one counter for client A, another for client B, and so on. Client A reaching its limit does not affect client B.

.NET builds this with `PartitionedRateLimiter`. You give it a function that reads the key from the request and says which limiter that key uses. It creates the limiter for a key the first time the key appears, and keeps it for later requests with the same key.

<!-- snippet: built-in-partitioned -->
```csharp
using var limiter = PartitionedRateLimiter.Create<string, string>(clientId =>
    RateLimitPartition.GetFixedWindowLimiter(clientId, _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = 1,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    }));

using var first = await limiter.AcquireAsync("client-a");
using var second = await limiter.AcquireAsync("client-a");
using var other = await limiter.AcquireAsync("client-b");

Console.WriteLine($"client-a first: {first.IsAcquired}, client-a second: {second.IsAcquired}, client-b: {other.IsAcquired}");
```
From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

It prints `client-a first: True, client-a second: False, client-b: True`: client A used its one permit, and client B still has its own.

**Why the key matters.** The key decides who shares a limit. Choose it with care:

- A key that is too wide (for example, one key for all anonymous users) puts many clients into one counter. They block each other.
- A key that is too narrow (for example, a new random value per request) gives every request its own counter. Nothing is limited, and memory grows with each new key.
- Each active key costs memory. This library's cost per partition is about 1 KB (**measured**, see [measurements.md](measurements.md#memory-per-partition)).

This library has its own helper for partitions, `ResilientRateLimitPartition.Get`. [03-api-reference.md](03-api-reference.md) explains it.

## One replica or many

**The problem.** Production services usually run more than one copy of the same program, to handle more traffic and to stay up when one copy fails. Each copy is called a [replica](#replica) (or an instance, or a pod in Kubernetes). A load balancer spreads requests over the replicas.

**What goes wrong with in-memory limiters.** The built-in limiters count in the memory of their own process. Each replica has its own counter and does not know what the other replicas counted. So a limit of "100 requests per minute" becomes 100 per minute *per replica*. With 3 replicas, a client can send up to 300 requests per minute. With 10 replicas, up to 1,000. The real limit grows each time you add a replica, and it changes when an autoscaler adds or removes replicas.

**The shared counter.** The fix is to keep the count in one place that all replicas can reach: a [shared counter](#shared-counter). Each replica asks the same counter, so the limit is the same no matter how many replicas run. The place that holds the shared counter is called the [store](#store) in this documentation.

**Redis.** [Redis](#redis) is the store most often used for this. Redis is a separate server program that keeps data in memory and answers over the network very fast (well under a millisecond on a local machine, **measured**, see [measurements.md](measurements.md#store-round-trip)). It can run a small script as one step that no other client can interrupt, so two replicas that count at the same moment cannot both see the old value. Cloud providers sell Redis as a managed service.

**RedisRateLimiting.** [RedisRateLimiting](#redisratelimiting) is an open-source NuGet package (not part of this library) that implements the .NET `RateLimiter` base class on top of Redis. It has `RedisFixedWindowRateLimiter`, `RedisSlidingWindowRateLimiter`, `RedisTokenBucketRateLimiter` and `RedisConcurrencyRateLimiter`. Because they are normal `RateLimiter` objects, you call `AcquireAsync` and read a lease as with the built-in limiters. The difference is that each call goes over the network to Redis.

That network call is also the weak point. A counter in local memory never fails to answer. A counter in another server can be slow, unreachable or down. The rest of this page is about that problem.

## Which limiter should I use?

You have five realistic choices. This table compares them, and a paragraph for each row follows.

| Option | What it gives | Pick it when |
|---|---|---|
| Built-in .NET limiters (`System.Threading.RateLimiting`, ASP.NET Core `AddRateLimiter`) | In-memory counting per process; no dependency; no network | One instance, or a per-replica limit is acceptable (the effective global limit then grows with the replica count) |
| `RedisRateLimiting` alone | One shared count across all replicas, stored in Redis | Many replicas must share one limit, and you accept what happens when Redis is slow or down (every request waits for or fails with Redis) |
| `ResilientRateLimiting` (core) + `RedisRateLimiting` | The shared count, plus timeout, circuit breaker, a chosen outage behaviour (local fallback / fail-open / fail-closed), recovery after an outage, source tag, metrics, Retry-After | Many replicas share a limit and a Redis problem must not become an outage or a flood |
| `ResilientRateLimiting.AspNetCore` on top | 429 + `Retry-After` defaults, degraded header, DI registration from configuration, logging | The limiter protects an ASP.NET Core app |
| None of these | — | Daily/monthly quotas, billing entitlements: use the business database |

**Built-in .NET limiters.** They count in the memory of each process. They need no extra server and no network call, so they are fast and they cannot fail because of another system. Use them when you run one instance. Use them also with many replicas if a limit per replica is good enough, for example when the only goal is to stop one replica from being overloaded. Remember that the total limit is then the per-replica limit times the number of replicas, and it changes when the replica count changes.

**`RedisRateLimiting` alone.** All replicas share one count in Redis, so the limit is the same for 1 replica or 20. The price is that every request now depends on Redis. We checked what happens when Redis cannot be reached (**measured**, see [measurements.md](measurements.md#redisratelimiting-alone-while-redis-is-unreachable); setup: `RedisRateLimiting` 1.2.1, StackExchange.Redis 2.9.11 with its default settings, `RedisSlidingWindowRateLimiter`, Redis 7 in local Docker, .NET 10 on Windows). Two cases were tried: nothing listening on the Redis port from the start, and a Redis container that answered normally and was then stopped. In both cases, each `AcquireAsync` call waited about 5 seconds (between 4,989 and 6,013 ms over 6 calls) and then threw `StackExchange.Redis.RedisConnectionException` ("The message timed out in the backlog attempting to send because no connection became available (5000ms)"). No lease came back. The 5 seconds is the Redis client's own default timeout, and the wait repeats for every request, because nothing remembers that Redis is down. So during a Redis outage every request waits about 5 seconds and then fails with an exception that your code must handle. In an ASP.NET Core app, an exception that nothing catches becomes an error response for the client (this last step was not measured here). Pick this option only if you accept that behaviour.

**`ResilientRateLimiting` core with `RedisRateLimiting`.** This library wraps the Redis limiter. The Redis limiter is still the one that counts, and on a normal day the answer is exactly the Redis answer. The library adds what happens around the call: a short **store timeout** (20 ms by default, a **starting point**, see [measurements.md](measurements.md#store-round-trip)), a **circuit breaker** that stops calling Redis for a while after repeated failures, and one outage behaviour that you choose. It also handles recovery after an outage, tags each lease with the path that answered, reports metrics, and computes a Retry-After value. Pick it when many replicas must share a limit and a Redis problem must turn neither into an outage (every request fails) nor into a flood (every request is allowed with no limit). The library counts nothing itself and depends on no Redis client: the core package references only `System.Threading.RateLimiting` and `Polly.Core`. Any `RateLimiter` that keeps its count in a store can be the primary. Redis through `RedisRateLimiting` is the one this project tests.

**`ResilientRateLimiting.AspNetCore` on top.** A second, small package for ASP.NET Core apps. It sets the rejection status code to 429 ("Too Many Requests"), writes the `Retry-After` header from the lease, can add an `X-RateLimit-Degraded: true` header to a rejection so the client knows a degraded path answered, registers the shared parts in dependency injection from your configuration, and logs store failures. Pick it when the limiter protects an ASP.NET Core app. [02-getting-started.md](02-getting-started.md) shows it.

**None of these.** A rate limiter is for overload protection: windows of seconds to minutes. It is not the right tool for daily or monthly quotas, or for what a customer has paid for. Those counts must survive restarts, must never reset by surprise, and often need an audit trail. Keep them in your business database, next to your authorization rules. [Choosing a window length](#choosing-a-window-length) explains why long windows are weak on both the local and the Redis side.

## When the shared store is slow or down

**What the caller sees without this library.** As measured in the previous section, a plain Redis limiter makes each request wait for the Redis client's timeout (about 5 seconds with default settings) and then throws. A slow Redis is similar: each request waits as long as Redis takes. Your service is now as available as Redis is. A limiter that was meant to protect the service has become a way to take it down.

**Three possible answers.** When the store does not answer, the limiter must still answer the request. There are only three sensible answers, and each has a risk. This library calls the choice [`StoreFailureBehavior`](#storefailurebehavior), and you set it per limiter:

| Behaviour | What happens during the outage | Risk |
|---|---|---|
| [Local fallback](#local-fallback) (`LocalFallback`, the default) | Each replica counts in its own memory, with a smaller limit, until the store is back | The total limit is only approximate: it depends on how many replicas run and how evenly traffic spreads over them |
| [Fail open](#fail-open) (`FailOpen`) | Every request is allowed | No limit at all while the store is down. A traffic spike during a Redis outage reaches your service unchecked |
| [Fail closed](#fail-closed) (`FailClosed`) | Every request is rejected | Your service is down for all clients while the store is down, even though the service itself is healthy |

**Local fallback** is the default because it keeps both properties roughly true: the service stays up, and traffic stays limited. The [local budget](#local-budget) for each replica is usually the shared limit divided by the typical replica count, rounded up. For example, a shared limit of 100 with 3 replicas gives each replica 34 (example numbers). The fallback limiter must be a window or token-bucket limiter. It cannot be a `ConcurrencyLimiter`, because a concurrency limiter gives its permit back when the lease ends, so it cannot remember recent traffic. A concurrency limiter can still be the primary with `FailOpen` or `FailClosed`.

**How the library notices a problem.** A store call counts as a [store failure](#store-failure) when:

- the store does not answer within the [store timeout](#store-timeout) (`StoreHealthOptions.StoreTimeout`, 20 ms by default, a **starting point**). The library stops waiting and answers from the configured behaviour at once; it does not wait for the Redis client's 5 seconds;
- the [circuit breaker](#circuit-breaker) is open. After enough recent calls have failed, the library stops calling the store for a while and treats each request as a store failure without any network call. [The circuit breaker](#the-circuit-breaker) explains when it opens and closes;
- the store limiter throws an exception that counts as a store failure, for example the `RedisConnectionException` above. By default, `ArgumentException`, `ObjectDisposedException` and `InvalidOperationException` are treated as bugs in the calling code and reach the caller; other exceptions count as store failures. You can change this rule with `StoreHealthOptions.ShouldHandle`.

A cancellation by the caller (the `CancellationToken` you pass to `AcquireAsync`) is never a store failure. It reaches the caller as a normal cancellation.

When the store answers in time, its answer is used as it is, allowed or rejected. A rejection by the store is a normal answer, not a failure.

**The source tag.** Every lease from this library says which path produced it. This is the [source tag](#source-tag), a `LeaseSource` value in the lease metadata: `Distributed` (the store answered), `LocalFallback`, `FailOpen`, `FailClosed`, or `Recovery` (the store is back, but the local counter answered; see [Recovery after an outage](#recovery-after-an-outage)). Your code, your metrics and your logs can tell a normal answer from a degraded one.

```mermaid
flowchart TD
    A["AcquireAsync(permits)"] --> B{"Circuit breaker open?"}
    B -- yes --> F["Store failure"]
    B -- no --> C["Call the store limiter<br/>(for example Redis)"]
    C --> D{"Answer within StoreTimeout?"}
    D -- "no: too slow" --> F
    D -- "yes: a lease" --> E["Lease from the store<br/>source: Distributed<br/>(allowed or rejected)"]
    D -- "yes: an exception" --> G{"Counts as a store failure?"}
    G -- yes --> F
    G -- "no (bug in the caller, or caller cancelled)" --> X["Exception reaches the caller"]
    F --> H{"FailureBehavior"}
    H -- LocalFallback --> I["Local fallback limiter answers<br/>source: LocalFallback"]
    H -- FailOpen --> J["Allowed<br/>source: FailOpen"]
    H -- FailClosed --> K["Rejected<br/>source: FailClosed"]
```

This flowchart leaves out one step. With `LocalFallback`, for a short time after an outage ends, the local counter is asked before the store; a refusal there has the source `Recovery`. [Recovery after an outage](#recovery-after-an-outage) explains why.

Choosing the behaviour takes one option. With `FailOpen`, no fallback limiter is needed:

<!-- snippet: fail-open -->
```csharp
var options = new ResilientRateLimiterOptions { FailureBehavior = StoreFailureBehavior.FailOpen };

using var limiter = primary.WithResilience(fallback: null, options, storeHealth);
```
From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

[02-getting-started.md](02-getting-started.md) builds a complete limiter with a local fallback. [04-configuration.md](04-configuration.md) explains every option.

## The circuit breaker

**The problem.** The [store timeout](#store-timeout) limits how long one request waits. But if the store is down, every request still starts a store call, waits the full timeout, and only then goes to the fallback path. Each request pays the timeout, and a store that is already in trouble keeps getting calls.

**What goes wrong without a breaker.** Every request is slower by the store timeout during the whole outage. The replicas keep calling a store that cannot answer, which makes the store's recovery harder.

**What the library does.** A [circuit breaker](#circuit-breaker) watches the recent store calls. When too many of them fail, it *opens*: for a while, the library does not call the store at all, and every request goes to the configured behaviour at once, with no wait. After [`BreakDuration`](#break-duration) (5 seconds by default, a **starting point**) the breaker lets a test call through. If that call works, the breaker *closes* and normal operation continues. If it fails, the breaker stays open for another `BreakDuration`. (This test-call cycle is how the underlying Polly circuit breaker works; this library configures it and does not change it.)

**When it opens.** Three settings on `StoreHealthOptions` decide this. The breaker opens when, within the last [`BreakerSamplingDuration`](#breaker-sampling-duration) (10 seconds by default):

1. at least `FailuresBeforeOpen` store calls happened (5 by default, minimum 2), **and**
2. the failed share of those calls is at least `FailureRatio` (0.5 by default, so "at least half").

The name `FailuresBeforeOpen` can mislead: it counts *calls*, not failures. It is the smallest number of calls the breaker needs to see before it may judge the store at all. Examples with the default values (all within 10 seconds):

| Store calls | Failed | Breaker |
|---|---|---|
| 4 | 4 | Stays closed: fewer than 5 calls, not enough to judge |
| 10 | 4 | Stays closed: 40% failed, less than half |
| 10 | 5 | Opens: exactly half failed, and "at least half" includes exactly half |
| 5 | 3 | Opens: 60% failed |

All defaults are **starting points**. A service with very little traffic may never reach 5 calls in 10 seconds; then only the store timeout protects it, and each request still pays it.

**What counts as a failure** is the list in [When the shared store is slow or down](#when-the-shared-store-is-slow-or-down): a timeout, an exception that counts as a store failure, or the breaker itself being open. A cancellation by the caller counts as a **success** for the breaker. The caller stopped waiting; that says nothing about the store, so it must not help to open the breaker.

**One breaker per store connection.** The breaker lives in [`StoreHealth`](#storehealth). Create one `StoreHealth` for each store connection (for example, one per Redis server), and give it to every limiter that uses that connection. Then all these limiters share one view of the store: when the store fails, the breaker opens for all of them at once, and none of them keeps calling it.

## The local budget

**The problem.** During an outage with [local fallback](#local-fallback), each replica counts alone. How large should each replica's limit be?

**What goes wrong with the wrong value.** If each replica keeps the full shared limit (say 100), then 3 replicas together allow 300 during the outage: the same multiplication as in [One replica or many](#one-replica-or-many). If each replica's limit is too small, clients get rejected during the outage although the service could handle them.

**What the library does.** `LocalBudget.ForReplicas(sharedPermitLimit, replicaCount)` computes the [local budget](#local-budget): the shared limit divided by the replica count, rounded up.

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

100 divided by 3 is 33.3, so each replica gets 34 (the sample prints `Local budget per replica: 34`). You pass the shared limit, not a fixed local number, so when you later raise the shared limit, the local budget follows it.

**Pass the typical replica count, not the maximum.** If your autoscaler runs between 2 and 10 replicas but usually 3, pass 3. The two possible mistakes are not equal:

| Replica count you pass is too low | Replica count you pass is too high |
|---|---|
| Each local budget is too large: the replicas together admit more than the shared limit during an outage | Each local budget is too small: legitimate clients are rejected during an outage |

Without this library, a Redis outage means either no limit or no service. Admitting somewhat more than the limit during an outage is a milder version of a problem you already accept. Rejecting good clients is a new problem that the library would add. So when you are not sure, choose the value that errs toward admitting more: the typical count, rounded up.

**Uneven load is yours to tune.** The division assumes that the load balancer spreads traffic evenly. If one replica gets much more traffic than the others (for example, because clients keep long-lived connections to one replica), that replica rejects earlier during an outage. The library cannot know this. If you see it, change the replica count you pass: a smaller count gives every replica a larger budget.

## Warm state

**The problem.** An outage starts in the middle of normal traffic. A client may have used 90 of its 100 permits this minute, all counted in Redis. If the local fallback limiter only starts to count when the outage starts, it begins empty, and the client gets a fresh local budget on top of what it already used.

**What goes wrong without warm state.** Each outage becomes a small gift of extra permits. With many clients and replicas, a service gets a burst of extra traffic at exactly the moment its store is in trouble.

**What the library does.** While the store is healthy, every request that the store allows is also charged to the local fallback limiter. The local limiter's answer is thrown away; the store's answer counts. So the local limiter always knows roughly what this replica admitted recently. This is [warm fallback state](#warm-fallback-state): when an outage begins, the fallback continues from the recent count instead of from zero.

This is also why the fallback must be a window or token-bucket limiter. A `ConcurrencyLimiter` gives its permit back when the lease is disposed, so after the request ends, nothing of the traffic remains.

**Limits on how long warm state is kept.** Keeping state for every client costs memory. A [partition](#partition) that has seen no traffic for a while is normally removed by `PartitionedRateLimiter`'s cleanup of idle limiters. The library tells the cleanup to keep a partition's limiter while it holds warm state, but only within two limits:

- **Time.** An idle partition keeps its warm state for at most [`MaxWarmRetention`](#warm-retention) (2 minutes by default, a **starting point**), or for `FallbackRecoveryTime` if that is shorter. After this time the local limiter would have refilled anyway, so the state has no more value.
- **Count.** When the number of live partitions on one store connection is above `StoreHealthOptions.MaxWarmPartitions` (10,000 by default), warm state is released early for *every* partition on that connection, not only the extra ones. One warm partition costs about 1 KB (**measured**, see [measurements.md](measurements.md#memory-per-partition)), so the default caps warm state at about 10 MB.

When warm state is released, the partition can be removed. If the same client comes back during an outage, its fallback limiter starts empty again. That is the price of bounded memory.

## Recovery after an outage

**The problem.** During the outage, each replica admitted requests from its local budget. Redis never saw those requests. When Redis comes back, it still has the count from before the outage, which is too low. The window has not moved on yet, so a client that used its local budget during the outage now has the old Redis count plus a fresh-looking remainder.

**What goes wrong without recovery handling.** Right after the outage, clients can get close to a second allowance within the same window: one from the local fallback, and one from Redis.

**What the library does.** When the store answers again after a fallback, the limiter enters [recovery mode](#recovery-mode) for `FallbackRecoveryTime`. In recovery mode, each request is first charged to the local counter (the **recovery gate**):

- If the local counter refuses, the request is rejected at once, without a store call. The lease has the source tag `Recovery`. It is not tagged `LocalFallback`, because the store did not fail: the store is up, and the local counter decided.
- If the local counter allows, the store is asked as usual, and the store's answer is used. The request is not charged to the local counter twice.
- If the request needs more permits than the local counter can ever grant, the store decides alone. The gate exists to stop the overshoot after an outage, not to replace the store.

After `FallbackRecoveryTime`, recovery mode ends and only the store decides again. Recovery mode exists only with `LocalFallback`; `FailOpen` and `FailClosed` never enter it.

**Why you must set `FallbackRecoveryTime`.** It is the time the fallback limiter needs to refill completely: the window length for a fixed or sliding window, or the time to refill a token bucket from empty. The library cannot read it from the fallback limiter, because the `RateLimiter` base class has no property for it. So `FallbackRecoveryTime` is required with `LocalFallback`, and a value above one day is rejected as a likely unit mistake. [04-configuration.md](04-configuration.md) shows how to compute it for each kind of limiter.

## Cold start

**The problem.** A replica starts (a deployment, a crash and restart, a new autoscaled pod) while Redis is down. Its local fallback limiter is empty. But an empty counter in a new process does not mean that this replica's share is unused: the previous process may have just used it.

**What goes wrong without a cold-start rule.** Each restart during an outage hands out a fresh local budget. The overshoot is the local budget times the number of restarts. Restarts often come together with outages (the same network problem can cause both), so this is not rare.

**What the library does.** `StoreHealthOptions.ColdStartFallbackFactor` (1.0 by default, which means no effect) makes each request cost more permits until the store has been reached. With a factor below 1.0, each request served by the local fallback is charged the permit count divided by the factor, rounded up. For a one-permit request and a factor of 0.5, that is 2 permits, so the replica serves about half its local budget. The limiter itself keeps its size; only the charge per request changes. If the larger charge is more than the fallback limiter can ever grant, the request is charged its normal permit count instead.

Cold start ends for every limiter on the same `StoreHealth` as soon as any one of them gets any answer from the store, even a rejection. A rejection also proves that the store is reachable and that the shared count is real.

## Retry-After

**The problem.** A rejected client needs to know when to try again. If it tries again at once, it is rejected again, and it adds load. If it waits too long, it loses time for nothing.

**What the header means.** HTTP has the `Retry-After` response header for this: "wait this many seconds, then try again". It is advice to the client, not a promise. The `ResilientRateLimiting.AspNetCore` package writes it from the lease on a 429 response. You can also read it yourself:

<!-- snippet: read-retry-after -->
```csharp
var hasRetryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter);

Console.WriteLine(hasRetryAfter ? $"Retry after: {retryAfter}" : "No Retry-After hint.");
```
From `samples/ResilientRateLimiting.Samples.Console/Scenarios.cs`

**Where the value comes from.** An allowed lease has no Retry-After value. For a rejected lease, the library builds the value in three steps:

1. **A base value.**
   - The rejecting limiter's own value, if it gives one. For example, the built-in `FixedWindowRateLimiter` gives the time until its window resets.
   - If a healthy store rejected without a value, there is **no** Retry-After at all. The library has nothing honest to estimate from. **Measured** (see [measurements.md](measurements.md#redisratelimiting-and-the-retry-after-value)): `RedisRateLimiting` 1.2.1's sliding window, fixed window and token bucket limiters give no value under the standard `MetadataName.RetryAfter` name. (The fixed window limiter uses its own metadata name, `RATELIMIT_RETRYAFTER`, which this library does not read.) So with these limiters, a rejection by a healthy Redis has no Retry-After.
   - On a degraded path (`LocalFallback`, `FailClosed`, `Recovery`) with no value from a limiter, an estimate: `FallbackRecoveryTime` if it is set, otherwise the breaker's `BreakDuration`.
2. **Added time, capped.** A random spread is always added, so that many rejected clients do not all return at the same moment and cause a new spike. While degraded, the spread is wider, and a longer wait (up to the base value again) is added too, because the store is already in trouble. Both additions together never exceed `MaxAddedRetryDelay` (60 seconds by default, a **starting point**). Set it to zero to add nothing.
3. **At least one second.** A smaller result is raised to one second.

For example, with `FailClosed`, no `FallbackRecoveryTime` and the default `BreakDuration` of 5 seconds, the base value is 5 seconds. The random spread adds 2 to 6 seconds, and the longer wait adds 5 more, so the client is told 12 to 16 seconds (computed from the code; the console sample printed `Retry after: 00:00:13.5968548` in one run).

## Choosing a window length

**The problem.** Nothing stops you from setting a window of one day or one month. The library allows it. But the longer the window, the more a single lost count costs, and there are more ways to lose a count than it first seems, on *both* sides.

**What gets weaker as the window grows:**

| Weakness with long windows | Local fallback | Distributed primary (Redis) |
|---|---|---|
| Restart clears the count | yes | no, Redis keeps it |
| Warm state kept only `MaxWarmRetention` | yes | no |
| Key eviction silently resets the count | no | yes |
| Memory grows with the limit (sliding window: one entry per allowed request) | small | yes |
| No audit trail | yes | yes |

- **Local fallback.** The count lives in process memory, so a restart clears it. Warm state is kept for at most `MaxWarmRetention` for an idle partition.
- **Redis.** Redis can run out of memory and then delete keys to make room. This is called [key eviction](#key-eviction), and it depends on the Redis configuration. A deleted key is a count that silently starts again at zero, with no error. A Redis sliding window also stores one entry per allowed request, so its memory grows with the limit.
- **Both.** Neither keeps an [audit trail](#audit-trail): a record of who used what and when. You cannot prove later why a client was allowed or rejected.

**A worked example.** Compare two limits (example numbers):

- **100 requests per minute.** If a restart or an eviction loses the count, the worst case is about one extra minute of traffic for that client: at most about 100 extra requests. The damage ends when the window moves on.
- **10,000 requests per day.** If a restart during an outage, or one eviction, loses the count in the afternoon, the client can get close to a second daily allowance: up to about 10,000 extra requests. Nobody notices, and nothing records it.

**So this library is for overload protection:** windows of seconds to minutes, where a lost count costs little and fixes itself soon. Daily or monthly [quotas](#quota) and billing entitlements (what a customer has paid for) belong in your business database, next to your authorization rules. There the count survives restarts, never disappears silently, and can be audited.

## Glossary

### AcquireAsync

The method you call on a `RateLimiter` to ask for permits: `AcquireAsync(permitCount, cancellationToken)`. It returns a [lease](#lease). With this library, always use `AcquireAsync`; the synchronous `AttemptAcquire` always rejects, because it does not call the store.

### Audit trail

A durable record of who used what and when. Neither a local limiter nor Redis keeps one; a business database can.

### Break duration

How long the [circuit breaker](#circuit-breaker) stays open before it lets a test call through: `StoreHealthOptions.BreakDuration`, 5 seconds by default (a starting point).

### Breaker sampling duration

The time span over which the [circuit breaker](#circuit-breaker) counts calls and failures: `StoreHealthOptions.BreakerSamplingDuration`, 10 seconds by default (a starting point).

### Circuit breaker

A guard that stops calling a store that keeps failing. After enough recent calls fail, it "opens" and every call counts as a [store failure](#store-failure) at once, with no network call. After a set time it lets calls through again to test the store. See [The circuit breaker](#the-circuit-breaker).

### Cold-start factor

`StoreHealthOptions.ColdStartFallbackFactor`. Until any limiter on the store connection has reached the store, each request served by the local fallback costs its permit count divided by this factor, rounded up. 1.0 (the default) means no effect.

### Concurrency limiter

A limiter that counts requests in progress at the same time, not requests over time. A permit comes back when its lease is disposed. In .NET: `ConcurrencyLimiter`.

### Degraded path

Any answer that did not come from a healthy store: source tag `LocalFallback`, `FailOpen`, `FailClosed` or `Recovery`.

### Fail closed

The outage behaviour that rejects every request while the store is failing. Safe for the protected resource; the service is unavailable to clients during the outage.

### Fail open

The outage behaviour that allows every request while the store is failing. The service stays available; there is no limit during the outage.

### Failure ratio

The share of store calls that must fail, within the [breaker sampling duration](#breaker-sampling-duration), before the breaker opens: `StoreHealthOptions.FailureRatio`, 0.5 by default. Exactly this share is enough.

### Failures before open

`StoreHealthOptions.FailuresBeforeOpen`: the smallest number of store calls (not failures) the breaker must see within the sampling duration before it may open. 5 by default, minimum 2.

### Fallback limiter

The in-memory window or token-bucket limiter that answers during an outage with [local fallback](#local-fallback). It also keeps [warm fallback state](#warm-fallback-state). Never a `ConcurrencyLimiter`.

### Fallback recovery time

`ResilientRateLimiterOptions.FallbackRecoveryTime`: how long the [fallback limiter](#fallback-limiter) needs to refill completely. It sets the length of [recovery mode](#recovery-mode) and the degraded Retry-After estimate. Required with `LocalFallback`; at most one day.

### Fixed window

A limiter that cuts time into equal blocks and allows up to the limit in each block. The count resets when the next block starts. In .NET: `FixedWindowRateLimiter`.

### Key eviction

Redis deleting keys to free memory when it is full, depending on its configuration. For a rate limiter, an evicted key is a count that silently restarts at zero.

### Lease

The answer from a rate limiter. `IsAcquired` is `true` when the request is allowed. A lease can carry [metadata](#metadata). Dispose it when the request is finished.

### Limit

The largest number of [permits](#permit) a limiter hands out in its window, bucket or at one time. For example, `PermitLimit = 100`.

### Local budget

The limit each replica's [local fallback](#local-fallback) limiter uses during an outage: usually the shared limit divided by the typical replica count, rounded up. `LocalBudget.ForReplicas` computes it.

### Local fallback

The default outage behaviour: each replica answers from its own in-memory limiter, with a smaller limit, while the store is failing. The source tag is `LocalFallback`.

### Metadata

Extra facts attached to a [lease](#lease), read with `lease.TryGetMetadata(...)`. Examples: the [Retry-After](#retry-after-header) value, and this library's [source tag](#source-tag).

### Outage

A time when the [store](#store) cannot answer: it is down, unreachable, or too slow.

### Overload protection

Using a rate limiter to keep a service from receiving more work than it can handle. Windows of seconds to minutes. This is what this library is for.

### Partition

A separate limiter for each [partition key](#partition-key), so that each client or tenant has its own count. In .NET: `PartitionedRateLimiter`.

### Partition key

The value that decides which [partition](#partition) a request belongs to, for example a client ID or tenant ID.

### Permit

One unit of a limit. Usually one request costs one permit.

### Primary limiter

The store-backed `RateLimiter` that this library wraps, for example a `RedisSlidingWindowRateLimiter`. It does the real counting while the store is healthy.

### Queue

A waiting line inside a limiter. With `QueueLimit` above zero, a request over the limit can wait for a permit instead of being rejected at once. `QueueLimit = 0` means reject at once.

### Quota

A long-term allowance, for example 10,000 requests per day or what a customer has paid for. Keep quotas in a business database, not in a rate limiter.

### Random spread

A random amount of time added to a Retry-After value, so that rejected clients do not all come back at the same moment. Also called jitter. Capped, together with the degraded extra wait, by `MaxAddedRetryDelay`.

### Rate limiter

A component that decides whether a request may go ahead, by counting requests against a [limit](#limit). In .NET, the base class is `RateLimiter` in `System.Threading.RateLimiting`.

### Recovery mode

A period of [fallback recovery time](#fallback-recovery-time) after the store answers again following a fallback. The local counter is asked first (the recovery gate); its refusals have the source tag `Recovery`.

### Redis

A separate server program that keeps data in memory and answers over the network very fast. Many replicas can share one Redis, which makes it a common [store](#store) for a [shared counter](#shared-counter).

### RedisRateLimiting

An open-source NuGet package, separate from this library, that implements .NET `RateLimiter` classes on top of [Redis](#redis): `RedisFixedWindowRateLimiter`, `RedisSlidingWindowRateLimiter`, `RedisTokenBucketRateLimiter` and `RedisConcurrencyRateLimiter`.

### Replica

One running copy of your service. Production services often run several replicas behind a load balancer.

### Retry-After header

An HTTP response header that tells the client how long to wait before it tries again. A limiter can put this value into the [lease](#lease) metadata (`MetadataName.RetryAfter`).

### Segment

One part of a [sliding window](#sliding-window). When a segment grows older than the window, the permits counted in it come back.

### Shared counter

One count that all [replicas](#replica) read and update, kept in a [store](#store), so the limit stays the same no matter how many replicas run.

### Sliding window

A limiter that allows up to the limit in any window of the given length, not only in fixed blocks. In .NET: `SlidingWindowRateLimiter`.

### Source tag

The `LeaseSource` value this library puts in every lease's metadata. It says which path answered: `Distributed`, `LocalFallback`, `FailOpen`, `FailClosed` or `Recovery`.

### Store

The external system that holds the [shared counter](#shared-counter), usually [Redis](#redis). The store limiter (also called the primary limiter) is the `RateLimiter` that talks to it.

### Store connection

One connection to one store, for example one Redis server. Each store connection gets one [`StoreHealth`](#storehealth).

### Store failure

A store call that did not give a usable answer: it was slower than the [store timeout](#store-timeout), the [circuit breaker](#circuit-breaker) was open, or the store limiter threw an exception that counts as a failure. A store failure sends the request to the configured [outage behaviour](#storefailurebehavior).

### Store timeout

How long the library waits for the store before it treats the call as a [store failure](#store-failure): `StoreHealthOptions.StoreTimeout`, 20 ms by default (a starting point).

### StoreFailureBehavior

The option that chooses what happens during a [store failure](#store-failure): `LocalFallback` (the default), `FailOpen` or `FailClosed`.

### StoreHealth

The object that holds the shared state for one [store connection](#store-connection): the store timeout, the circuit breaker, cold-start state and the partition count. Create one per store connection and pass it to every limiter that uses that connection.

### Token bucket

A limiter with a bucket of tokens. Each request takes tokens, and a fixed number is added back each period, up to the bucket size. In .NET: `TokenBucketRateLimiter`.

### Warm fallback state

The recent count the [fallback limiter](#fallback-limiter) keeps while the store is healthy, so it does not start empty when an outage begins.

### Warm retention

How long an idle partition keeps its [warm fallback state](#warm-fallback-state): `MaxWarmRetention` (2 minutes by default, a starting point) or the fallback recovery time, whichever is shorter.

Next: [02-getting-started.md](02-getting-started.md)
