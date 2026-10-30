# Benchmarks

`Queryeables.Benchmarks` measures what Queryeables adds on top of the libraries it uses, with
[BenchmarkDotNet](https://benchmarkdotnet.org), on an in-memory SQLite database. It is a console project outside the test
suite.

```bash
dotnet run -c Release --project Queryeables.Benchmarks -- --filter "*"
dotnet run -c Release --project Queryeables.Benchmarks -- --filter "*Remote*" --job short
```

Always run in `Release`, on a machine doing nothing else, and compare numbers only between runs on the same machine.

## What is measured

| Class | Question it answers |
| --- | --- |
| `StoreBenchmarks` | What does `IEntityStore` cost over Dommel and over hand-written SQL with Dapper? One row by key, 1000 rows, an insert and a delete (the store version runs them in a transaction that returns a `DbResult`). The baseline is Dommel called directly |
| `CacheBenchmarks` | What does a cache save? `Find` against a database read by key, the cost of a snapshot (`Entities`), and of a full reload, for 1 000 and 100 000 rows |
| `RemoteBenchmarks` | What does the remote layer cost without the network? Predicate translation, MessagePack, access policy and dispatcher, in process, against the local store. Add your network round trip to these numbers |

Every class runs `MemoryDiagnoser`: read the `Allocated` column next to the time.

## How to read the results

- **Store overhead**: the `Ratio` of `Store_*` to `Dommel_*`. The store adds a factory call, an `await using` and, for
  transactions, the `DbResult`; the database work is the same.
- **Cache**: `Cache_Find` against `Store_FirstOrDefault` is what a cached lookup saves per read. `Cache_Load` is the
  price of a full reload, paid at startup and after a change on a type without an integer key and without `[LastModified]`.
- **Remote**: `Remote_*` minus `Local_*` is the serialization and dispatch cost per call. Batching writes in a transaction
  sends one call for all of them.

## First results

`StoreBenchmarks`, 1 000 rows, .NET 10, Release, before `CapturedValues` (October 2026):

| Method | Mean | Allocated | Ratio |
| --- | ---: | ---: | ---: |
| `Dapper_FirstOrDefault` (hand-written SQL) | 49.3 µs | 2.41 KB | 0.19 |
| `Dommel_FirstOrDefault` (baseline) | 261.3 µs | 9.46 KB | 1.00 |
| `Store_FirstOrDefault` | 261.7 µs | 9.50 KB | 1.00 |
| `Dapper_GetAll` | 906.6 µs | 142.98 KB | 3.47 |
| `Dommel_GetAll` | 932.1 µs | 143.03 KB | 3.57 |
| `Store_GetAll` | 935.3 µs | 143.06 KB | 3.58 |
| `Dommel_InsertDelete` | 67.2 µs | 4.20 KB | 0.26 |
| `Store_InsertDelete_InTransaction` | 74.6 µs | 8.38 KB | 0.29 |

- The store adds nothing measurable to Dommel on reads; on writes, the difference is the transaction (`BEGIN`/`COMMIT`
  and the `DbResult`) that the Dommel row does not open.
- A read by key with a predicate cost five times hand-written SQL: Dommel compiled a lambda to read the captured `id`
  on every query (about 210 µs and 7 KB). `DommelEntityStore` now replaces captured variables with constants before
  handing the predicate to Dommel (`CapturedValues`); the remote filters do the same.
- Reading many rows costs the same with the three: the time is in the database and the materialization.

After `CapturedValues`, same machine:

| Method | Mean | Allocated | Ratio |
| --- | ---: | ---: | ---: |
| `Dapper_FirstOrDefault` | 51.6 µs | 2.41 KB | 0.19 |
| `Dommel_FirstOrDefault` (baseline, a captured variable) | 269.1 µs | 9.47 KB | 1.00 |
| `Store_FirstOrDefault` | 64.0 µs | 5.67 KB | 0.24 |

A read through the store is now four times faster than Dommel called with the same predicate, and 12 µs from
hand-written SQL: the remaining cost is Dommel building the SQL text on every call. Since then, a predicate comparing one
column with a value (a key, a foreign key) runs SQL built once per type and column (`EqualityQueries`):

| Method | Mean | Allocated | Ratio |
| --- | ---: | ---: | ---: |
| `Dapper_FirstOrDefault` | 52.9 µs | 2.41 KB | 0.20 |
| `Dommel_FirstOrDefault` | 270.1 µs | 9.47 KB | 1.00 |
| `Store_FirstOrDefault` | 64.8 µs | 3.83 KB | 0.24 |

A read by key through `IEntityStore` is now within a few microseconds of hand-written SQL with Dapper, and about four
times faster than Dommel called with the same predicate (262 µs and 9.5 KB at first). On this machine, two runs differ
by up to 5 µs on this row (standard deviation 4.7 µs): compare allocations rather than times between close results.

What remains above Dapper (about 1.4 KB) is what the store adds by design: the store and its connection created by the
factory, the async methods of the interface, the named parameter. On a database across a network, a round trip costs
300 to 2 000 µs.

Results are published with each release in the release notes.
