# Security

## What never crosses the wire

No SQL, no expression tree, no type name chosen by the client. A remote filter is a list of "property, operator, value"
conditions: the server checks the property on the exposed type, the operator against a whitelist, converts the value to
the property type, and builds the query itself. At most 1000 conditions, 1000 values in one `In`, 2000 values in the
whole filter, and 32 levels of AND/OR; a page starts at most 1,000,000 rows in; a batch holds at most 100 operations
(`server.MaxBatchSize`).

## Columns a client cannot use

What a client cannot read, it cannot search or sort on either: otherwise it would guess a hidden value one comparison at
a time ("the secret starts with a", "with ab"...). A property left out by the serializer (`[JsonIgnore]`,
`[IgnoreMember]`, `[IgnoreDataMember]`) or marked `[NotQueryable]` answers "unknown property" in a filter or a sort.

A `[ServerOwned]` property never takes its value from a client: on an insert it is reset to its default, on an update
the stored value is kept, whatever was sent. For a creator, an approval flag, a role, a balance: set them in the
server's own code or in the database. The same goes for a numeric key: an insert from a client is numbered by the database,
whatever key it sent (a key the application assigns itself keeps its value: `[DatabaseGenerated(DatabaseGeneratedOption.None)]`,
or a Guid).

### A value that never leaves the server

Hide it with the attribute **of the serializer the server uses**: `[IgnoreMember]` for MessagePack (MagicOnion), `[JsonIgnore]`
for System.Text.Json. MessagePack ignores `[JsonIgnore]`: a server that would send a property marked for another serializer
refuses to serve the type (`NotSupported`, and the reason in its log) instead of sending the value. An update keeps the
stored value of a hidden property: the client never had it, so what it sends back is not its decision. A custom
`IEntitySerializer` says what it leaves out with `Omits(PropertyInfo)`.

An update reads the stored row (its `[ServerOwned]` values, and its owner with a row filter) and writes it back in one
transaction. Under READ COMMITTED (PostgreSQL, SQL Server), the server's own code may still change the row between the
two: give such an entity a version (`[ConcurrencyCheck] public long Version`), and a client update made against the old row becomes a `Conflict`
instead of writing an old balance back. An entity saved by its own transactional service (`IAggregateRepository`) is
checked before the service's transaction: its service decides.

```csharp
public class Account
{
    public long Id { get; set; }
    [IgnoreMember] public string PasswordHash { get; set; } = "";     // MessagePack; [JsonIgnore] with System.Text.Json
    [NotQueryable] public string InternalNote { get; set; } = "";
    [ServerOwned] public string? Role { get; set; }
}
```

## Errors

An unexpected error (a database error, a bug) reaches the client as "an unexpected error occurred on the server
(reference 3f2a...)": database messages name tables, constraints and values. The exception is logged on the server under
that reference (category `Queryeables.Remote.EntityRequestDispatcher`). `server.DetailedErrors = true` sends the message
itself: development only. Validation errors, conflicts and refusals keep their own message.

## Access policies

Every exposed type has a policy; there is no default:

| Policy | Allows |
| --- | --- |
| `EntityAccessPolicies.Authenticated` | Everything, to an authenticated user |
| `EntityAccessPolicies.ReadOnly` | Reads, to an authenticated user |
| `EntityAccessPolicies.From(request => ...)` | Your rule: `request.Operation`, `request.EntityType`, `request.Entity` (for writes), `request.User` |
| `EntityAccessPolicies.AllowAll` | Everything, even anonymous: tests and closed networks only |

```csharp
var policy = EntityAccessPolicies.From(r =>
    r.User?.Identity?.IsAuthenticated == true &&
    (r.Operation == EntityOperation.Read || r.User.IsInRole("admin")));
```

A policy decides per type and operation: it sees the entity of a write, but not the rows of a read. To keep each user to
its own rows, add a row filter.

## Row filters

```csharp
server.Expose<Order>(EntityAccessPolicies.Authenticated,
    rows: RowLevelSecurity.For<Order, long>(o => o.Id, user =>
    {
        var tenant = user?.FindFirst("tenant")?.Value;
        return o => o.TenantId == tenant;
    }));
```

The server adds the filter to every read (all rows, filters, pages, changes) and checks every write:

| Operation | Rule |
| --- | --- |
| Read | Only the user's rows, whatever the client's filter |
| Insert | The new row must be one of the user's rows, otherwise `Forbidden` |
| Update | The stored row must be one of the user's rows (otherwise `NotFound`, as if it did not exist), and so must the new content (otherwise `Forbidden`: a row cannot be given away) |
| Delete | The stored row must be one of the user's rows, otherwise `NotFound` |

The filter is built for each call from the authenticated user; an anonymous call gets `null`, and `_ => false` refuses it.
The children of a graph sent by a client follow the rules of their own type too (see [graphs](#graphs)).

The stored row is checked, then written: give such types a `[ConcurrencyCheck]` version. A row changed between the check
and the write (moved to another tenant by someone else, say) then fails the write with `Conflict` instead of being
written over.

## A change of user on a client

Row filters protect the server; the client must also forget what it kept. Caches and the offline copy hold the rows of
the signed-in user: after a sign-out or a sign-in, call `services.ChangeUserAsync()` (see
[offline mode](offline-mode.md#a-change-of-user)). It empties every cache and the offline copy before loading anything
for the new user, and refuses while offline writes of the previous user wait to be sent.

## Size of reads

A page is cut to `maxPageSize` (1000). A read that is not paged (every row, a filter, the changes since a date) fails with
`InvalidRequest` beyond `maxReadSize` (10,000 by default) instead of sending the table: the client pages, or narrows its
filter. Both are set per type: `Expose<T>(policy, maxPageSize: 200, maxReadSize: 2000)`. With a row filter, only the
user's rows count, and only they are read: the filter and the limit are part of the query (one row beyond the limit at
most), the changes since a date included, so that a user cannot make the server read the whole table.

The user is the one ASP.NET Core authenticated for the call: configure authentication on the server
(`AddAuthentication`, `UseAuthentication`), and send credentials from the client.

## Data kept on the device

The offline copy is not encrypted by default. For sensitive data, wrap its storage in `EncryptedOfflineStorage` with a
key per user from the platform's key store (see [offline mode](offline-mode.md#encryption-at-rest)).

## Graphs

A graph saved remotely is checked as its parent (policy, row filter), and each child as its own type, in the same
transaction: anything refused rolls back the whole graph.

| Child | Rule |
| --- | --- |
| Any | `[ServerOwned]` reset on insert and kept on update; a property the serializer leaves out kept on update; validated (`Contacts[2]: The Name field is required.`) |
| Type exposed | Its policy for the insert, update or delete of each child (a parent removed checks the deletion of each child), and its row filter as on a direct write: `NotFound` for a stored child out of reach, `Forbidden` for content outside the user's rows |
| Key of another parent | Rejected (`Conflict`): a client cannot take over rows through a graph |

A child type that is not exposed has no policy of its own: the parent's applies.

## Notification hubs

Hubs broadcast keys of changed rows. Protect them like the data: `[Authorize]` on the MagicOnion hub class,
`.RequireAuthorization()` on the SignalR `MapHub`. They refuse anonymous connections by default; for public data only,
`services.AddSingleton(new ChangeHubOptions { AllowAnonymous = true })`.

What a client sends a hub is not trusted:

- The changes a client reports are not relayed: only the server's database is a source. A client with a database of its
  own, shared with others, needs `services.AddSingleton(new ChangeHubOptions { RelayClientChanges = true })`; its changes
  are then checked (a row, never "reload everything"; a key of at most 256 characters) and limited (20 per second per
  connection).
- A write names its author's connection, so that the author does not get its own change back: the name is believed only
  when that connection belongs to the caller. The connections left out are never sent to the clients.
- The queue of changes is bounded (10,000). When clients cannot keep up, it is emptied and one "synchronize" is sent:
  each cache catches up from the database, and memory stays bounded.

**Limit:** every subscriber of a type receives the keys of all its changed rows, whatever its row filter: no content, but
a tenant can see that rows of another changed, and when. Where that matters, give each tenant its own server (or its own
hub type), until notifications are filtered per tenant.

## TLS

Clients validate the server certificate as the system does, on every platform: chain, expiry, host name.
`validateCertificate` replaces that decision (to accept a known self-signed certificate by its hash, say) and is applied
on every platform; a handler that cannot use it is refused rather than silently ignored. To pin a certificate and still
refuse an invalid one, check `errors == SslPolicyErrors.None` in it too.

## Logs

`logCommands: true` logs each SQL command at `Debug`, with `@Name` in place of the values. `logParameterValues: true` writes
the values: names, e-mails and passwords would reach the log, so keep it for debugging.

## Secrets

Keep connection strings and certificate passwords out of the source code: user secrets in development, environment
variables or a vault in production. Never commit them, and change any that was.
