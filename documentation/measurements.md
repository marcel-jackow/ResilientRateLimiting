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

## What was not measured

- Server GC (`ServerGarbageCollection` was off for every run on this page).
- Linux hosts (all runs above were on Windows).
- TLS to a cloud-hosted Redis (all runs above used a local, unencrypted connection).
- Sliding-window and token-bucket fallback limiters (the memory table above used a fixed-window fallback only).

Next: `01-concepts.md`, which starts from the basic ideas — rate limiter, permit, lease, partition — that this page assumes.
