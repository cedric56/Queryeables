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
| `StoreBenchmarks` | What does `IEntityStore` cost over Dommel on the same connection type? One row by key, 1000 rows, an insert and a delete (the store version runs them in a transaction that returns a `DbResult`). The baseline is Dommel called directly |
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

Results are published with each release in the release notes.
