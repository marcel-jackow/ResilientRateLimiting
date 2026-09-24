# Measurements

This page reports numbers from real runs against a real Redis, not numbers guessed from theory. Every default that claims to be "measured" elsewhere in this documentation links back to a table on this page. You can rerun the whole page yourself. Docker must be running.

```bash
for i in 1 2 3; do dotnet run -c Release --project benchmarks/ResilientRateLimiting.Measurements -- footprint-redis; done
for i in 1 2 3; do dotnet run -c Release --project benchmarks/ResilientRateLimiting.Measurements -- footprint-memory; done
dotnet run -c Release --project benchmarks/ResilientRateLimiting.Measurements -- roundtrip
```

Each command prints a small machine table, then a result table. The tables below are the numbers this page reports.

## Memory per partition

A **partition** (see the glossary at `01-concepts.md#glossary`) is one key that the rate limiter tracks separately — for example, one customer or one API key. Each partition holds its own `ResilientRateLimiter` state: a primary limiter, a local fallback limiter, and a small amount of book-keeping. If that per-partition cost is too high, a service with many active partitions can run out of memory. This is why `StoreHealthOptions.MaxWarmPartitions` exists: above that many live partitions, the library releases warm fallback state early to cap total memory.

Machine used for the runs on this page:

| Field | Value |
|---|---|
| Date | 2026-09-24 |
| OS | Microsoft Windows 10.0.26200 |
| Runtime | .NET 10.0.12 |
| Logical processors | 20 |
| GC | workstation |
| Redis | redis:7-alpine in a local Docker container |

Result (median of three separate process runs, each with 10,000 partitions, one request per partition):

| Primary | Fallback | Partitions | Bytes per partition after creation (median) | Bytes per partition after one request each (median) |
|---|---|---|---|---|
| RedisSlidingWindowRateLimiter | FixedWindowRateLimiter | 10,000 | 1,023 | 1,027 |
| FixedWindowRateLimiter (in memory) | FixedWindowRateLimiter | 10,000 | 936 | 936 |

**Measured**: about 1 KB per partition. So 10,000 warm partitions use about 10 MB — small enough to keep on one process. This is why `MaxWarmPartitions` defaults to 10,000: a service that stays at or below that count never needs to think about this number.

Two things to read honestly into this table. First, the primary limiter here is `RedisSlidingWindowRateLimiter` (or, for the in-memory row, a `FixedWindowRateLimiter` standing in for it) and the fallback is a `FixedWindowRateLimiter`. A fixed-window fallback is the cheap case: it only needs to remember a count and a window start. A sliding-window or token-bucket fallback needs to remember more (a timestamp per recent permit, or a fractional balance), so it costs more per partition. That cost was not measured here. Second, both rows measure the same shape of workload — one request per partition — so this table is about the cost of holding a partition open, not about throughput.

Why a console program instead of BenchmarkDotNet: BenchmarkDotNet's memory diagnoser reports bytes *allocated* per operation, which is not the same as bytes *kept alive* per partition, and xUnit runs test classes in parallel with a runner that allocates on the same process heap, which would pollute a heap-size measurement taken during a test run. A single console process per scenario, with `GC.GetTotalMemory(true)` read before and after building all 10,000 partitions, measures only what is kept alive, undisturbed by a parallel test runner.

## Store round trip

A **round trip** is the time from calling `AcquireAsync` to getting the lease back, including the time spent waiting on the store (Redis). This section measures that time so `StoreHealthOptions.StoreTimeout` can be set from real numbers instead of a guess.

Result (5,000 samples after a 500-call warm-up, single process, same machine as above):

| Call | Samples | p50 ms | p90 ms | p99 ms | p99.9 ms | max ms |
|---|---|---|---|---|---|---|
| Redis PING (network floor) | 5,000 | 0.38 | 0.55 | 1.10 | 3.47 | 6.44 |
| RedisSlidingWindowRateLimiter alone | 5,000 | 0.41 | 0.52 | 0.80 | 3.60 | 5.07 |
| ResilientRateLimiter, default StoreTimeout | 5,000 | 0.42 | 0.54 | 0.87 | 3.13 | 5.08 |

Requests not answered by the store at the default `StoreTimeout` (warm-up included): 0 of 5,500.

**Measured**, with an important caveat: this was measured against Redis in a local Docker container, so there is almost no network between the process and the store — the "Redis PING" row above is close to the practical floor for this setup. A managed Redis in the same cloud region usually adds about 1–3 ms at p50 (not measured here, since it needs a live cloud resource). Across regions — calling a store in a different part of the world — it is tens of milliseconds.

This matters for `StoreTimeout`. The default, 20 ms, is a **starting point**, not a measured production value: it is well above the local p99 measured here (well under 1 ms), but a production deployment sits on different hardware, a different network path, and a different Redis tier. The right way to pick `StoreTimeout` is to measure your own p99 round trip in your production environment and set `StoreTimeout` to about five times that — high enough that ordinary latency spikes do not get treated as a store failure, low enough that a genuinely slow or down store is still noticed quickly.

One more measured number, because a fast timeout is only as accurate as the timer under it. A 20 ms `Task.Delay` does not always come back in 20 ms: on Windows, without raising the OS timer resolution, sleeps and delays are rounded up to the system timer's tick. Folklore quotes 15.6 ms as the tick size, so a 20 ms delay would be expected to complete after one or two ticks — around 16–31 ms. Rather than repeat that number, this was checked directly: a 20-line script (`Task.Delay(TimeSpan.FromMilliseconds(20))`, 200 times, in the session scratchpad, not part of this repository) measured a median of 31.82 ms and a maximum of 35.98 ms on this machine. So the folklore figure is roughly right in shape but the actual median was higher than 15.6 ms alone would suggest. Practically: on Windows, a `StoreTimeout` near the 20 ms default can itself be inflated by 10–15 ms of pure timer rounding, on top of the real network wait — one more reason to measure your own environment (which may well be Linux in production, where timer resolution is finer) rather than trust either the default or this note.

## RedisRateLimiting alone while Redis is unreachable

This section answers one question for `01-concepts.md#which-limiter-should-i-use`: what does a plain `RedisRateLimiting` limiter, with no `ResilientRateLimiting` around it, do when Redis cannot be reached?

Setup (2026-09-24, same machine as above): a small script outside this repository (a .NET 10 file-based app in the session scratchpad), `RedisRateLimiting` 1.2.1, StackExchange.Redis 2.9.11 with its default settings, one `RedisSlidingWindowRateLimiter` (limit 100 per minute), three timed `AcquireAsync(1)` calls per case.

- **Case A, Redis down from the start:** the connection string points at a port where nothing listens, with `abortConnect=false` so the program can start.
- **Case B, Redis stops while in use:** a `redis:7-alpine` container answers three calls normally (6 ms, then 0 ms, 0 ms), then the container is stopped with `docker stop`, and three more calls follow.

| Case | Call | Time until the call ended | Result |
|---|---|---|---|
| A | 1 | 6,013 ms | `StackExchange.Redis.RedisConnectionException` |
| A | 2 | 5,996 ms | `RedisConnectionException` |
| A | 3 | 4,999 ms | `RedisConnectionException` |
| B, after stop | 1 | 5,804 ms | `RedisConnectionException` |
| B, after stop | 2 | 4,989 ms | `RedisConnectionException` |
| B, after stop | 3 | 5,002 ms | `RedisConnectionException` |

The exception message was "The message timed out in the backlog attempting to send because no connection became available (5000ms)". No call returned a lease.

**Measured:** while Redis cannot be reached, every call waits about 5 seconds (the Redis client's default timeout) and then throws. Nothing remembers that Redis is down, so the next call waits again. This is the behaviour `ResilientRateLimiting` replaces with a short store timeout, a circuit breaker and a chosen outage behaviour.

## RedisRateLimiting and the Retry-After value

This section supports `01-concepts.md#retry-after`: does a healthy Redis limiter give a Retry-After value when it rejects?

Setup (2026-09-24, same machine, a script outside this repository): `RedisRateLimiting` 1.2.1 against a `redis:7-alpine` container. For each of `RedisSlidingWindowRateLimiter`, `RedisFixedWindowRateLimiter` and `RedisTokenBucketRateLimiter` with a limit of 1, two `AcquireAsync(1)` calls; the second is rejected, and its metadata is read.

| Limiter | Metadata names on the rejected lease | Value under the standard `MetadataName.RetryAfter` |
|---|---|---|
| `RedisSlidingWindowRateLimiter` | `RATELIMIT_LIMIT`, `RATELIMIT_REMAINING` | none |
| `RedisFixedWindowRateLimiter` | `RATELIMIT_LIMIT`, `RATELIMIT_REMAINING`, `RATELIMIT_RETRYAFTER` | none |
| `RedisTokenBucketRateLimiter` | `RATELIMIT_LIMIT`, `RATELIMIT_REMAINING` | none |

**Measured:** none of the three gives a value under the standard name. The fixed window limiter uses its own name, `RATELIMIT_RETRYAFTER`, which `ResilientRateLimiting` does not read. So a rejection by a healthy Redis carries no Retry-After from this library.

## Redis keys, their type and their expiry

This section supports [06-production.md](06-production.md#memory-in-redis) and [06-production.md](06-production.md#redis-eviction-and-the-silent-failure): what does `RedisRateLimiting` actually store per partition, and does it set an expiry?

Setup (2026-09-24, same machine, a script outside this repository): `RedisRateLimiting` 1.2.1 against a `redis:7-alpine` container, one `RedisSlidingWindowRateLimiter` and one `RedisFixedWindowRateLimiter`, both `PermitLimit = 5` and `Window = 30 s`, one `AcquireAsync(1)` call on each, then `KEYS *`, `TYPE`, `TTL` and, for the sorted set, `ZCARD` on every key found.

| Limiter | Keys | Redis type | Content after one allowed request | TTL |
|---|---|---|---|---|
| `RedisSlidingWindowRateLimiter` | `rl:sw:{key}` | sorted set | one entry (`ZCARD` = 1) | ~30 s (the window) |
| `RedisSlidingWindowRateLimiter` | `rl:sw:{key}:stats` | hash | `total_successful` = 1 | ~30 s |
| `RedisFixedWindowRateLimiter` | `rl:fw:{key}` | string | the count, as text (`"1"`) | ~30 s |
| `RedisFixedWindowRateLimiter` | `rl:fw:{key}:exp` | string | a stored expiry timestamp | ~30 s |

**Measured:** every key `RedisRateLimiting` writes carries a TTL close to the configured window; none is left to grow old forever. The sliding window keeps one sorted-set entry per allowed request (so its size is bounded by `PermitLimit`, not by how many requests were rejected), plus one small hash for its own bookkeeping. The fixed window keeps a single integer per partition. `{key}` above is the partition key wrapped in literal curly braces, which is a Redis cluster hash tag: it keeps both keys for one partition on the same cluster node.

## What was not measured

- Server GC (`ServerGarbageCollection` was off for every run on this page).
- Linux hosts (all runs above were on Windows).
- TLS to a cloud-hosted Redis (all runs above used a local, unencrypted connection).
- Sliding-window and token-bucket fallback limiters (the memory table above used a fixed-window fallback only).

Next: `01-concepts.md`, which starts from the basic ideas — rate limiter, permit, lease, partition — that this page assumes.
