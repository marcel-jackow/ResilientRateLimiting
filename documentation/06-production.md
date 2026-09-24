# Production

This page is about running the library against a real Redis, day after day, not about the ideas behind it (that is [01-concepts.md](01-concepts.md)) or the exact members you call (that is [03-api-reference.md](03-api-reference.md)). It collects what an operator needs to know before the library sees real traffic: one failure Redis can produce that the library cannot see at all, how much memory the library uses on both sides of the connection, the two timeouts that must agree with each other, and the small amount of over-counting the design accepts on purpose. If a word is new, its full explanation is in the [glossary](01-concepts.md#glossary) in [01-concepts.md](01-concepts.md).

## Checklist

- Pick a Redis [eviction](#redis-eviction-and-the-silent-failure) policy on purpose, and watch `evicted_keys`.
- Size memory for [Redis](#memory-in-redis) and for [your own process](#memory-in-your-process).
- Set the Redis client's own timeout [at or below `StoreTimeout`](#timeouts).
- Know the [restart overshoot bound](#restarts-during-an-outage) if you deploy or crash during an outage.
- Know that a [slow store can over-count](#a-slow-store-can-count-requests-the-fallback-served) by a small, bounded amount, never under-count.
- If you run on [Azure](#hosting-redis-on-azure), plan for the move from Azure Cache for Redis to Azure Managed Redis.
- Read the [known limits](#known-limits) before you assume something the library does not do.
- Watch the two metrics from [05-telemetry.md](05-telemetry.md#metrics): a rising `store_failures` count or a `source` other than `distributed` means the store is not being trusted, even while every request still gets an answer.

## Redis eviction and the silent failure

**Problem.** Redis can run low on memory. When it does, its own configuration decides what happens next: it can refuse new writes, or it can delete some existing keys to make room. This deleting is called **key eviction** (see the [glossary](01-concepts.md#key-eviction)).

**What goes wrong without knowing this.** Every rate-limit key this library's store writes carries a time-to-live, so it is a normal candidate for eviction under any policy that considers expiring keys. If Redis deletes a rate-limit key to free memory, the next request for that [partition](01-concepts.md#partition) simply starts a new key at zero. This looks exactly like a healthy, empty counter: no timeout, no exception, no open [circuit breaker](01-concepts.md#circuit-breaker), no metric. The [store failure](01-concepts.md#store-failure) machinery in this library exists to notice a store that answers late or with an error — it cannot notice a store that answers on time with a wrong, too-generous count. A limit that looked correctly enforced can have been silently reset many times.

**What the library does.** Nothing, and it cannot: from the library's side, an evicted key and a genuinely idle partition are indistinguishable. This is a property of Redis, not of this library, so the fix is operational, not code:

- **Know your eviction policy.** `volatile-lru` (deletes only keys that carry a time-to-live, oldest first) is the default eviction policy on Azure Cache for Redis. Some platforms configure `allkeys-lru` instead (any key is a candidate, whether or not it carries a time-to-live). Either way, this library's keys are eligible, because every one of them carries a time-to-live (**measured**, see [measurements.md](measurements.md#redis-keys-their-type-and-their-expiry)).
- **Prefer a dedicated Redis instance for rate limiting**, with its `maxmemory-policy` set to `noeviction`. This is a recommendation, not a requirement: the library works correctly against a shared Redis used for other things too. On a dedicated instance with `noeviction`, exhausting memory produces a write error instead of a silent reset, and a write error is an exception the library's normal classification treats as a store failure — handled by the circuit breaker, answered by the chosen [outage](01-concepts.md#outage) behaviour, and counted in the `store_failures` metric, exactly like a network problem.
- **Monitor `evicted_keys`.** Redis reports this counter itself (`redis-cli INFO stats`, or the equivalent metric on a managed Redis). A rising count on an instance that also serves rate-limit keys is a sign that some of those keys may have been deleted before their time. Nothing inside this library can raise that alarm for you.

## Memory in Redis

**Problem.** Once eviction is on your radar, the next question is how much memory the rate limiter actually asks Redis to hold, and whether that grows with traffic in a way that could reach the eviction threshold above.

**What the library does.** This library does not talk to Redis directly; it wraps a limiter from the `RedisRateLimiting` package, and that package decides what to store. Two shapes exist, verified against `RedisRateLimiting` 1.2.1 (**measured**, see [measurements.md](measurements.md#redis-keys-their-type-and-their-expiry)):

- **Sliding window** (`RedisSlidingWindowRateLimiter`): one entry in a Redis sorted set for every *allowed* request, plus one small hash for its own bookkeeping. A rejected request adds nothing. So the number of entries for one partition is at most its permit limit, and across a whole service it is at most `PermitLimit × active partitions` — never more, because only allowed requests are recorded and the limit caps how many can be allowed per window.
- **Fixed window** (`RedisFixedWindowRateLimiter`): a single integer per partition, plus one small key holding when that window ends. This costs roughly `PermitLimit` times less memory than the sliding window per partition, because it keeps one number instead of one entry per request.

Both key families carry a time-to-live close to the window length, so they expire by themselves when a partition stops being used — the same time-to-live that makes them eviction candidates above.

**The keys.** For an operator who wants to measure or alert on rate-limiting memory separately from the rest of a shared Redis instance, the key prefixes are `rl:sw:{key}` and `rl:sw:{key}:stats` for the sliding window, and `rl:fw:{key}` and `rl:fw:{key}:exp` for the fixed window, where `{key}` is your partition key wrapped in literal curly braces (a Redis cluster hash tag, so both keys for one partition always land on the same cluster node). `redis-cli --scan --pattern 'rl:*'` lists them on a shared instance.

## Memory in your process

**Problem.** Redis is not the only place state lives. Each [partition](01-concepts.md#partition) also holds a small amount of state in your own process — a primary limiter reference, a local fallback limiter, and some book-keeping — for as long as it is warm (see [Warm state](01-concepts.md#warm-state)).

**What the library does.** One warm partition costs about 1 KB of process memory (**measured**, see [measurements.md](measurements.md#memory-per-partition); the exact figure depends on which fallback limiter you chose). `StoreHealthOptions.MaxWarmPartitions` (10,000 by default, a **starting point**) caps how many partitions stay warm at once on one store connection: above that count, warm state is released early for every partition on that connection, capping this cost at roughly `MaxWarmPartitions × 1 KB` — about 10 MB at the default. Raise it only if you have measured your own per-partition cost and know you can afford more live partitions; lower it if your process runs under a tight memory limit and many distinct partition keys (for example, per-IP limiting) are expected.

## Timeouts

**Problem.** This library stops waiting for the store after `StoreHealthOptions.StoreTimeout` (20 milliseconds by default, a **starting point**) and switches to the fallback path. But the underlying Redis client — `StackExchange.Redis`, used by `RedisRateLimiting` — has its own, separate timeout, called `AsyncTimeout` (5 seconds by default). These two timeouts are unrelated unless you set them to agree.

**What goes wrong without setting them together.** `StoreTimeout` only decides how long *this library* waits before answering the caller from the fallback path; it cannot make the Redis client itself stop the call it already sent. If `AsyncTimeout` is left at its own default of 5 seconds while `StoreTimeout` is 20 milliseconds, every slow or unreachable call still occupies a connection in the Redis client for up to 5 seconds after this library has already answered the caller from the fallback path. Under a real outage, many such abandoned calls collect in the client at the same time, which can exhaust the client's connection pool — a store problem that becomes a client problem too, the exact result the [circuit breaker](01-concepts.md#circuit-breaker) and `StoreTimeout` exist to prevent.

**What to do.** Set the Redis client's own timeout at or below `StoreTimeout`. With `StackExchange.Redis`, that is `AsyncTimeout`:

<!-- snippet: web-async-timeout -->
```csharp
var redisOptions = ConfigurationOptions.Parse(redisConnectionString);
redisOptions.AsyncTimeout = 20; // milliseconds; stays at or below StoreTimeout so a slow store never blocks a request past the configured limit.
```
From `samples/ResilientRateLimiting.Samples.Web/Program.cs`

With `AsyncTimeout` at or below `StoreTimeout`, the Redis client releases an abandoned call at roughly the same time this library stops waiting for it, instead of long after. The call already sent to Redis may still complete and be counted there after both timeouts pass — see [A slow store can count requests the fallback served](#a-slow-store-can-count-requests-the-fallback-served) — but the connection is no longer kept open waiting for it.

## Restarts during an outage

**Problem.** During an [outage](01-concepts.md#outage), each replica answers from its own [local budget](01-concepts.md#local-budget) — a share of the shared limit, computed by `LocalBudget.ForReplicas` (see [Sizing](03-api-reference.md#sizing)). That budget lives only in the replica's memory.

**What goes wrong.** If a replica restarts (a crash, a deployment, or the platform replacing it) while the outage is still active, its local budget resets to a fresh, empty one — the new process has no memory of what the old one already admitted. So that replica's true admitted count for the outage is its budget before the restart, plus its (again full) budget after. Restarting more than once during the same outage repeats this every time.

**The bound.** Restart overshoot = fallback budget × number of restarts during the outage. A replica with a local budget of 34 that restarts twice during one outage will overshoot by 68 extra requests (calculated as `34 × 2 = 68`), so it can admit up to 102 total requests in that outage instead of 34 (calculated as `34 × 3 = 102`).

This is worth taking seriously rather than dismissing as unlikely, because the two events are not independent: whatever took the store down — a network problem, a bad deployment, a region-wide event — is often the same kind of event that also restarts replicas (a rollback, nodes being drained, a crash loop). Expect restarts and outages to happen together more often than chance alone would suggest.

The library gives you one knob to soften the first restart of a deployment or crash loop: `ColdStartFallbackFactor` (see [Cold start](01-concepts.md#cold-start) and `StoreHealthOptions.ColdStartFallbackFactor` in [04-configuration.md](04-configuration.md)). It shrinks the local budget of a process that has never yet reached the store, on the reasoning that such a process cannot tell "there has been little traffic" from "I have no idea yet." There is no default value that fits every deployment — the library will not invent one — so if you know you deploy during incidents, set it yourself.

There is nowhere safe to put the missing state instead: the store is down by definition, a dead container's local disk is gone with it, and having replicas coordinate directly with each other would need a second, independent system doing the same job Redis already does, with its own failure modes to manage. The bound above is accepted rather than solved.

## A slow store can count requests the fallback served

**Problem.** `StoreTimeout` is a hard cutoff (see [Timeouts](#timeouts) above): once it passes, the library abandons waiting for the store and answers from the fallback path. But the request the library already sent to Redis keeps running on the Redis side — a Lua script that started executing does not stop because the caller stopped waiting for its result.

**What this means.** Three things can happen to that abandoned call, and only one of them causes a mismatch:

| What the fallback decided | What the store records | Result |
|---|---|---|
| Admitted | one request admitted | no mismatch — the store's count is correct anyway |
| Rejected (the local budget was already spent) | one request admitted | the store's count is one higher than what was actually served |
| Would have been rejected by the store too | nothing recorded | no mismatch |

So the only drift is: for every request that the fallback rejected while the store was slow, the store may still record it as admitted. This makes the limiter **stricter for the rest of the window, never looser** — the store's count only ever goes up from this, so once the store is trusted again it will refuse slightly earlier than the true traffic would justify, not later. For a component whose entire purpose is to stop too many requests, over-counting is the safe direction to be wrong in; under-counting would not be.

**Why it cannot be undone.** Removing the extra entry would need an identifier that only the `RedisRateLimiting` package's own script generates and never exposes, and reaching into another package's Redis keys to delete data it wrote is not a coupling this library accepts. The drift is bounded instead: it clears on its own once the window slides, because a new window starts its count from zero regardless of what the old one held.

**What reduces it.** The number of abandoned calls during a slow period is bounded by how long the store stays slow and how much traffic arrives during that time, so a shorter `StoreTimeout` and a shorter client timeout (see [Timeouts](#timeouts)) both shrink the window in which this can happen. The [circuit breaker](01-concepts.md#circuit-breaker) bounds it further: once it opens, calls stop reaching the store at all, so nothing more can be over-counted there until the breaker allows one test call again.

## Hosting Redis on Azure

If you host the store on Azure: Azure Cache for Redis is being retired, with Microsoft recommending a move to Azure Managed Redis. Plan any new deployment around Azure Managed Redis rather than Azure Cache for Redis, and if you already run on Azure Cache for Redis, treat the migration as expected work rather than something that may never happen. Whichever one you use, the eviction policy and memory guidance above applies the same way: check the configured `maxmemory-policy`, prefer a dedicated instance with `noeviction` where you can, and monitor `evicted_keys`.

## Known limits

- **Nesting one `ResilientRateLimiter` inside another is unsupported.** It is not prevented at compile time or at run time, but it is not a supported configuration: the inner lease's [source tag](01-concepts.md#source-tag) would be hidden by the outer one, so you could no longer tell from the lease whether the answer came from the store or from a fallback. Wrap each underlying limiter with exactly one `ResilientRateLimiter`.
- **A `ConcurrencyLimiter` cannot be the fallback limiter.** This one *is* enforced: constructing a `ResilientRateLimiter` (or calling `WithResilience`, or `ResilientRateLimitPartition.Get`) with a `ConcurrencyLimiter` as the fallback throws `ArgumentException` immediately (`src/ResilientRateLimiting/LocalMirror.cs:22-27`). The reason is the same one covered in [Warm state](01-concepts.md#warm-state): a `ConcurrencyLimiter` returns its permit as soon as the lease is disposed, so it cannot hold [warm fallback state](01-concepts.md#warm-fallback-state) between requests, which the fallback path needs.
- **Call `AddResilientRateLimiting` once per application.** A second call throws `InvalidOperationException`, because it would register a second, unkeyed `StoreHealth`, and dependency injection would then hand every policy the last one registered — including policies meant for the first store. For a second store connection, build its `StoreHealth` by hand and capture it in the policy that needs it, as shown under [`WithLogging`](03-api-reference.md#withlogging) in [03-api-reference.md](03-api-reference.md).

Next: [01-concepts.md](01-concepts.md) — if anything above assumed an idea you have not read yet, that page starts from the basic ones: rate limiter, permit, lease, partition.
