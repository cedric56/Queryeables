# Security

A server publishes **read-only replicas**: a client reads the rows of an exposed type its user may read (the policy, then
the row filter), the changes since a date, and a row by key; nothing else. It writes through the services of the
application, whose rules are yours: validate there, check the user there, as in any API.

## What never crosses the wire

No SQL, no expression tree, no type name chosen by the client, no write. A row is read by key: one "property = value"
condition per column of the key, nothing else (`InvalidRequest`). The server converts each value to the type of its
column and builds the query itself.

### A value that never leaves the server

Hide it with the attribute **of the serializer the server uses**: `[JsonIgnore]` for JSON (the default of MagicOnion),
`[IgnoreMember]` for MessagePack. MessagePack ignores `[JsonIgnore]`: a server that would send a property marked for another
serializer refuses to serve the type instead of sending the value: `app.MapQueryeables()` throws at startup with the reason
(a transport without it answers `NotSupported` to each request for the type, the reason in its log). A custom
`IEntitySerializer` says what it leaves out with `Omits(PropertyInfo)`.

```csharp
public class Account
{
    public long Id { get; set; }
    [JsonIgnore] public string PasswordHash { get; set; } = "";       // JSON; [IgnoreMember] with MessagePack
}
```

## Errors

An unexpected error (a database error, a bug) reaches the client as "an unexpected error occurred on the server
(reference 3f2a...)": database messages name tables, constraints and values. The exception is logged on the server under
that reference (category `Queryeables.Remote.EntityRequestDispatcher`). `server.DetailedErrors = true` sends the message
itself: development only. Refusals keep their own message.

## Access policies

Every exposed type has a policy; there is no default:

| Policy | Allows |
| --- | --- |
| `EntityAccessPolicies.Authenticated` | Reads, to an authenticated user |
| `EntityAccessPolicies.From(request => ...)` | Your rule: `request.EntityType`, `request.User` |
| `EntityAccessPolicies.AllowAll` | Reads, even anonymous: public data, tests and closed networks only |

```csharp
var policy = EntityAccessPolicies.From(r => r.User?.IsInRole("sales") == true);
```

A policy decides per type: it does not see the rows. To keep each user to its own rows, add a row filter.

## Row filters

```csharp
server.Expose<Order>(EntityAccessPolicies.Authenticated,
    rows: RowLevelSecurity.For<Order, long>(o => o.Id, user =>
    {
        var tenant = user?.FindFirst("tenant")?.Value;
        return o => o.TenantId == tenant;
    }));
```

The server adds the filter to every read: all the rows, a row by key, the changes since a date. A row that leaves the
user's rows leaves the replica when its change is notified (the client reads it again, and gets nothing). The filter is
built for each call from the authenticated user; an anonymous call gets `null`, and `_ => false` refuses it. A filter
through a relation (`o => o.Customer.TenantId == tenant`) reads the related type only if it is exposed and readable by the
user, and only its rows the user may read count.

## Size of a replica

A read fails with `InvalidRequest` beyond `maxReadSize` (10,000 by default) instead of sending the table, or a part of it:
such a type is read through a service of the application. Set it per type: `Expose<T>(policy, maxReadSize: 2000)`. With a
row filter, only the user's rows count, and only they are read: the filter and the limit are part of the query (one row
beyond the limit at most), the changes since a date included.

The user is the one ASP.NET Core authenticated for the call: configure authentication on the server
(`AddAuthentication`, `UseAuthentication`), and send credentials from the client.

## A change of user on a client

Row filters protect the server; the client must also forget what it kept. The caches hold the rows of the signed-in user:
after a sign-out or a sign-in, call `services.ChangeUserAsync()`. It empties every cache before loading anything for the
new user. A local database synchronized by Datasync is the application's to clear.

## Data kept on the device

The caches are in memory. A local database (Datasync) is a file of the application: encrypt it with the means of its
provider (SQLCipher for SQLite) when the data is sensitive.

## Notification hubs

Hubs broadcast keys of changed rows. Protect them like the data: `[Authorize]` on the MagicOnion hub class,
`.RequireAuthorization()` on the SignalR `MapHub`. They refuse anonymous connections by default; for public data only,
`services.AddSingleton(new ChangeHubOptions { AllowAnonymous = true })`.

A client sends a hub nothing but its subscription: only the server's database is a source of changes.

- The queue of changes is bounded (10,000). When clients cannot keep up, it is emptied and one "synchronize" is sent:
  each cache catches up from the database, and memory stays bounded.

By default, every subscriber of a type receives the keys of all its changed rows, whatever its row filter: no content,
but a tenant can see that rows of another changed, and when. Where that matters, set `ChangeHubOptions.Visibility`: a
change then reaches only the subscribers it accepts, usually by applying the type's row-level security to the row.

```csharp
services.AddSingleton(new ChangeHubOptions
{
    Visibility = async (change, ct) =>
        change.User?.FindFirst("tenant")?.Value is { } tenant
        && await tenants.OwnsAsync(tenant, change.EntityType, change.EntityKey, ct),   // a lookup by key: keep it fast
});
```

A "synchronize everything" names no row and still reaches every subscriber. A user who could see a row before a change
but not after is not told of that change, and keeps the row until its cache refreshes.

With a rule, a change is sent to the connections it accepted, named one by one (SignalR `Clients.Clients`, MagicOnion
`Only`): never to a connection it did not check, one that arrived after the check, or one of another server behind a
backplane (Redis). Each server then tells its own subscribers: in a farm, every server must receive the changes, from the
database (the PostgreSQL listener) or from a feed they share.

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
