# Queryeables.Generated

The source generator of [Queryeables](https://github.com/cedric56/Queryeables). It is not published on its own: it ships in
the `analyzers` folder of the [`Queryeables`](https://github.com/cedric56/Queryeables/blob/master/Queryeables.Core/README.md) package, so every project that references a
provider runs it.

## What it writes

For every parent of an `[EntityGraph]` collection:

- `I{Parent}Graph` / `{Parent}Graph`: sets the navigation properties after loading; it receives the cache of each
  looked-up type (`LookupNavigation`);
- `I{Parent}Repository` / `{Parent}Repository`: `SaveAsync`, `RemoveAsync`, `LoadChildrenAsync`, and one `Load{Collection}Async`
  per collection. Both classes are `partial`.

For every project with entities, a public module `Queryeables.Generated.Modules.Entities_{Assembly}`; and in every project
that references Queryeables, `AddGeneratedEntities()` on `QueryeablesBuilder`, which calls the module of the project and the
modules of the projects it references. Entities kept in a shared library are thus registered by the client and by the
server with the same call.

## Conventions

| Setting | Default |
| --- | --- |
| Key of an entity | The property marked `[System.ComponentModel.DataAnnotations.Key]`, otherwise `Id` (inherited ones included) |
| `ForeignKey` (child → parent) | `Id{Parent}` or `{Parent}Id` |
| `LookupForeignKey` (child → looked-up entity) | `Id{Navigation}` or `{Navigation}Id` |

## Diagnostics

| Id | Problem |
| --- | --- |
| QRY001 | The entity has no key |
| QRY002 | `[EntityGraph]` on a property that is not a writable collection of entities |
| QRY003 | The foreign key of the children was not found |
| QRY004 | A property named in the attribute does not exist |
| QRY005 | The foreign key type does not match the parent key |
| QRY006 | The key of the looked-up entity was not found |
| QRY007 | The lookup foreign key type does not match the looked-up key |
| QRY008 | `ParentNavigation` cannot hold the parent |
| QRY009 | `LookupForeignKey` is the child's own key |

The generator targets netstandard2.0 (Roslyn requirement) and never throws: every problem is a diagnostic.
