# Traces and metrics

Queryeables records what it does through the .NET diagnostics APIs, which OpenTelemetry, `dotnet-counters`,
`dotnet-trace` and Application Insights read: an `ActivitySource` and a `Meter`, both named `Queryeables`
(`QueryeablesDiagnostics.Name`). While nobody listens, nothing is recorded and nothing is timed.

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(QueryeablesDiagnostics.Name).AddAspNetCoreInstrumentation())
    .WithMetrics(m => m.AddMeter(QueryeablesDiagnostics.Name));
```

Without OpenTelemetry, from a terminal:

```bash
dotnet-counters monitor --process-id <pid> --counters Queryeables
```

## Activities

| Name | Kind | When | Tags |
| --- | --- | --- | --- |
| `queryeables.remote` | Client | Each call of a remote store (`RemoteEntityStore`) | `queryeables.operation`, `queryeables.type`, `queryeables.outcome` |
| `queryeables.server` | Server | Each request handled by `EntityRequestDispatcher` | same |
| `queryeables.cache` | Internal | A cache load, synchronization or row refresh | `queryeables.type`, `queryeables.kind` (`load`, `sync`, `row`) |
| `queryeables.offline.sync` | Internal | `OfflineMode.GoOnlineAsync` | `queryeables.sent`, `queryeables.conflicts` |

An activity whose outcome is not `ok` has the `Error` status: a failed request shows in red in a trace viewer.

## Metrics

| Instrument | Unit | Tags |
| --- | --- | --- |
| `queryeables.remote.duration` (histogram) | ms | `queryeables.operation`, `queryeables.type`, `queryeables.outcome` |
| `queryeables.server.duration` (histogram) | ms | same |
| `queryeables.changes.published` (counter) | changes | `queryeables.table`, `queryeables.change` (`Insert`, `Update`, `Delete`) |
| `queryeables.cache.refreshes` (counter) | refreshes | `queryeables.type`, `queryeables.kind` |
| `queryeables.offline.sent` (counter) | writes | `queryeables.type` |
| `queryeables.offline.conflicts` (counter) | writes | `queryeables.type`, `queryeables.outcome` |

Operations: `get_all`, `select`, `select_paged`, `get_changes`, `insert`, `update`, `delete`, `batch`. Outcomes: `ok`,
an error kind (`validation`, `conflict`, `not_found`, `forbidden`, `invalid_request`, `unknown_type`...), `unreachable`
(the call threw: no network, timeout) or `cancelled`.

## What to watch

- **`queryeables.remote.duration` by `queryeables.outcome`**: `unreachable` rising is a network problem before users
  report it; `conflict` rising is users overwriting each other.
- **`queryeables.server.duration` p95 by `queryeables.type`**: the slow entity types.
- **`queryeables.cache.refreshes` with `kind=load`**: a cache reloading often means its change feed misses changes, or
  reconnects too often.
- **`queryeables.offline.conflicts`**: how often offline work collides with someone else's.
