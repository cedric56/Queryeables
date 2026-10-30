---
_layout: landing
title: Queryeables
---

# Queryeables

**One data-access API for .NET, local or remote: your entities, their caches and their children, the same code everywhere.**

A view model or a service asks for `IPersonService` or `IEntityCache<Person, long>`. It never knows whether the data comes from
SQLite through Dommel, from SQL Server through EF Core, or from a server across the network.

```csharp
services.UseQueryeables(q => q
    .UseSqlite("Data Source=app.db")         // or UseMagicOnion("https://server:5001")
    .AddGeneratedEntities());
```

- [Introduction](introduction.md): what Queryeables is, and when to choose something else.
- [Getting started](getting-started.md): a working application in five steps.
- [Guides](guides/stores-and-transactions.md): stores, graphs, caches, remote access, notifications, security.
- [Benchmarks](benchmarks.md): what the abstraction costs.
- [API reference](api/Queryeables.html).

> **Status:** early. The API may still change before 1.0.
