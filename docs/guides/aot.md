# Native AOT and trimming

Every library sets `IsAotCompatible` and builds with the trimming and AOT analyzers without warnings. The source generator
replaces the reflection-based registration of caches and graphs.

What still generates code at run time, in the libraries Queryeables builds on:

| Component | Trimming | Full Native AOT |
| --- | --- | --- |
| Queryeables contracts, caches, graphs, remote core | Yes | Yes |
| Dommel / Dapper | Yes | No (Dapper emits IL) |
| EF Core | Yes | No (precompiled queries are experimental) |
| MessagePack | Yes | With the MessagePack source generator (`partial` types) |
| MagicOnion client | Yes | With the MagicOnion source generator |

A Native AOT client therefore talks to a server (`UseMagicOnion`) rather than to a local database through Dommel or EF Core.

The CI proves it: `Queryeables.AotSmoke` uses the generated graph repository and registration, a cache and its change feed,
transactions, and a remote store with nested filters and a batch; it is published with Native AOT (a trimming or AOT
warning fails the build) and the native binary is run.
