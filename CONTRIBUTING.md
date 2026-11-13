# Contributing

Issues and pull requests are welcome. Please open an issue to discuss larger changes first.

## Building and testing

The projects target **.NET 10**.

```bash
dotnet build Queryeables.slnx
dotnet test Queryeables.slnx
dotnet run --project Samples/Queryeables.Sample
dotnet run -c Release --project Queryeables.Benchmarks -- --filter "*"
```

`Queryeables.Replication` builds against `src/Routeables.Remote` of [Routeables.SignalR](https://github.com/cedric56/Routeables.SignalR),
cloned next to this repository (`../Routeables.SignalR`, or `-p:RouteablesRemoteRoot=<folder>/`). Without it, that project
and its tests are left out with a warning; the CI clones the commit set in `ROUTEABLES_SIGNALR_REF` (`.github/workflows/ci.yml`).
To build everything, clone both side by side:

```bash
git clone https://github.com/cedric56/Queryeables.git
git clone https://github.com/cedric56/Routeables.SignalR.git
cd Queryeables && dotnet test
```

A new `IEntityStore` must pass the contract tests (`Queryeables.Tests/Contract`): add it to `StoreImplementations.cs`.

`ConvergenceTests` run several clients against one server, online and offline, with networks coming and going and
answers lost; a failure prints its seed. `QUERYEABLES_LOAD_SCALE=10` multiplies the clients and the steps (the CI uses 5).

Some tests need a server. Without its variable they are reported as skipped, not passed (`[ServerFact]` and
`[RequiresServer]` on the class); the CI sets both:

| Variable | Server | Example |
| --- | --- | --- |
| `QUERYEABLES_POSTGRES` | PostgreSQL | `docker run -d -p 5432:5432 -e POSTGRES_PASSWORD=<password> postgres:17`, then `Host=localhost;Username=postgres;Password=<password>` |
| `QUERYEABLES_MONGODB` | MongoDB, as a replica set (transactions, change streams) | see [Queryeables.MongoDB](Queryeables.MongoDB/README.md#transactions), then `mongodb://localhost:27017/?replicaSet=rs0&directConnection=true` |

## Rules of the code

- `Queryeables.Abstraction` has no package reference.
- Every library is `IsAotCompatible` and builds without warnings (`TreatWarningsAsErrors`).
- Comments, messages, logs and documentation are in English.
- The generator reports problems as diagnostics and never throws; a new rule gets a QRY id and a line in
  `AnalyzerReleases.Unshipped.md`.

## Packages

`Queryeables.Core` is packed as the **`Queryeables`** package from `Queryeables.Core/Queryeables.nuspec`: it holds the
Abstraction, Remote and Core assemblies and the generator. When a `PackageReference` of one of those projects changes,
change the nuspec too. The other projects are packed as they are, each with the `README.md` of its folder.

## Releasing

1. Move the `Unreleased` section of `CHANGELOG.md` under the new version.
2. Tag the commit: `git tag v0.1.0 && git push origin v0.1.0`.
3. The CI builds, tests, packs and pushes every package to nuget.org with Trusted Publishing: no API key is stored. The
   nuget.org account `cedricb` has a trusted publishing policy for `cedric56/Queryeables`, workflow `ci.yml`.
