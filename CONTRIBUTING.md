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

A new `IEntityStore` must pass the contract tests (`Queryeables.Tests/Contract`): add it to `StoreImplementations.cs`.

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
3. The CI builds, tests, packs and pushes every package to nuget.org. It needs the `NUGET_API_KEY` repository secret
   (an API key from nuget.org with the "Push" scope for the `Queryeables*` glob).
