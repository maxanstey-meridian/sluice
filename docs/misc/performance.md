# Performance

Measured with BenchmarkDotNet on an Apple M1 Ultra and .NET 10, with the cache in memory only (no Redis). The
benchmarks are in [`benchmarks/`](https://github.com/maxanstey-meridian/sluice/tree/main/benchmarks); run them with
`task bench`.

## The short version

- **A cache hit costs about 0.2 µs for each dependency the value recorded.** FusionCache checks every tag of an entry
  on every hit, so this is the cost of tagging itself. Sluice adds about 15% on top.
- **Keep each value's dependencies to tens, not thousands.** A value built from 1,000 rows read one by one, each
  recorded with its own `Invoice.For(id)`, costs about 0.24 ms per hit: as much as a database query. Read such a list
  with one query or one `read.Children`, or record the source's `All`, so the value has one dependency.
- **With EF Core, a save costs about 7 µs more, and a cache miss about 32 µs more with a plain query, or 47 µs more
  through a read helper.** Against a database across a network, where a query takes hundreds of microseconds, that's
  small, and only misses pay it.

## Cache hits

A hit returns the cached value. "FusionCache, same tags" is FusionCache on its own, given as many tags as Sluice
records for the same dependencies.

| Dependencies | FusionCache, no tags | FusionCache, same tags | Sluice | Sluice allocates |
|---:|---:|---:|---:|---:|
| 1 | 0.18 µs | 0.54 µs | 0.67 µs | 1.0 KB |
| 10 | 0.17 µs | 2.3 µs | 2.7 µs | 3.2 KB |
| 100 | 0.17 µs | 20 µs | 23 µs | 25 KB |
| 1,000 | 0.17 µs | 211 µs | 239 µs | 250 KB |

## Cache misses

A miss runs the compute and stores its value. Here the compute returns a constant, so the table shows only the cost of
recording and storing; your fetch comes on top.

| Dependencies | FusionCache, no tags | FusionCache, same tags | Sluice |
|---:|---:|---:|---:|
| 1 | 0.58 µs | 1.5 µs | 1.1 µs |
| 10 | 0.60 µs | 5.7 µs | 2.6 µs |
| 100 | 0.58 µs | 48 µs | 24 µs |
| 1,000 | 0.59 µs | 488 µs | 234 µs |

## Invalidation

Invalidating one key removes two tags: about 1.1 µs through Sluice, against 0.57 µs for FusionCache removing the same
two itself. That includes scheduling its second publish 5 to 6 s later: invalidations due in the same second share
one timer, and each tag goes out once. With Redis, each tag is also a Redis write and a backplane message; see
[When Redis is slow or down](/guides/configuration#when-redis-is-slow-or-down).

## EF Core

On in-memory SQLite, the fastest database there is, so Sluice's share is as large as it gets. Sluice's misses
include opening the compute's own DI scope, building its context from the pool's options without the caller's async
state (a compute's context never comes from the pool), and checking the value before it's stored:

| | Without Sluice | With Sluice |
|---|---:|---:|
| Save one changed row | 11.3 µs | 17.8 µs |
| Cache miss that reads one row | 14.5 µs (FusionCache and a plain query) | 46.7 µs (a plain query on `read.Db`) |
| | | 61.7 µs (`read.Entity`) |

The save pays for capturing what changed and invalidating it: the row's key, and here its one
unique column, which adds two tags (about 0.9 µs). A plain query pays for reading its tag and recording
the entity types it names; working out what a query reads happens once, when EF compiles it. A miss through `read.Entity`
costs about 15 µs more than the same miss through a plain query.

## Not measured

- **Redis.** A server that hasn't looked a tag up yet reads it from Redis, then keeps it in memory. So the first hit
  of a value on a fresh server can cost one Redis lookup per tag, and later hits cost what's above.
- **Contention.** These run one call at a time.
