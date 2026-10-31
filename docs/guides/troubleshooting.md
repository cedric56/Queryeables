# Troubleshooting

### The children are not saved remotely

The server saved the parent without its graph repository. Check that the server calls `AddGeneratedEntities()` and that the
entities are in a library it references (the generator registers the entities of referenced projects). Since this check
exists, `Expose<T>` fails at startup in that case.

### Saving a parent deleted its children

The collection was an empty list instead of null: an entity read from a cache, with `= []` on the property. Declare
`List<Contact>? Contacts { get; set; }` without an initializer, and load the children before editing them.

### The cache does not follow the database

- The change feed listens to a table name: the `[Table]` name of the type, otherwise its name. Dommel pluralizes table names
  by default; name the table with `[Table]`.
- With SQLite, only changes made through the application's connections are reported.
- Subscribe to `RefreshFailed` on `EntityCache` to see the errors of background updates.

### `The SSL connection could not be established` / frame size errors

The client uses `https://` on a port where the server listens in plain HTTP (or the reverse). Match the scheme with the
Kestrel endpoint.

### `Predicate cannot be sent to the server`

The remote store accepts comparisons, booleans, `Contains` on a list, and `StartsWith`, `EndsWith`, `Contains` on strings,
combined with `&&`, `||` and `!`, nested up to 32 levels. It cannot negate a `Contains`, a text search, or an ordering on a
nullable property, nor compare two properties. Split the query, filter the rest in memory, or write a dedicated server
method.

### See the SQL

`q.UseSqlite(cs, logCommands: true)` (also `UseDommel`, `UseNpgsql`) logs every command with its parameters, in the
`Queryeables.SQL` category. For development only.
