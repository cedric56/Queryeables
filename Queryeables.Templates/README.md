# Queryeables.Templates

`dotnet new` templates for [Queryeables](https://github.com/cedric56/Queryeables). Targets **.NET 10**.

```bash
dotnet new install Queryeables.Templates

dotnet new queryeables-console -n MyApp          # SQLite, a cache that follows the table, a person and its contacts
dotnet new queryeables-client-server -n Shop     # Shop.Server (MagicOnion), Shop.Client (replicas), Shop.Shared (entities, IPeopleService)
```

`--QueryeablesVersion 0.1.0` picks the version of the Queryeables packages (the latest one by default, previews included).

Before the packages are published, build them from the sources: see
[contributing](https://github.com/cedric56/Queryeables/blob/master/CONTRIBUTING.md#trying-the-packages-before-they-are-published).
