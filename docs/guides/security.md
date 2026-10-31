# Security

## What never crosses the wire

No SQL, no expression tree, no type name chosen by the client. A remote filter is a list of "property, operator, value"
conditions: the server checks the property on the exposed type, the operator against a whitelist, converts the value to
the property type, and builds the query itself. At most 1000 conditions and 1000 `Contains` values per request.

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
    rows: EntityRowFilter.For<Order, long>(o => o.Id, user =>
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
The children of a graph follow their parent: expose a child type with its own filter when clients reach it directly.

## Size of reads

A page is cut to `maxPageSize` (1000). A read that is not paged (every row, a filter, the changes since a date) fails with
`InvalidRequest` beyond `maxReadSize` (10,000 by default) instead of sending the table: the client pages, or narrows its
filter. Both are set per type: `Expose<T>(policy, maxPageSize: 200, maxReadSize: 2000)`. With a row filter, only the
user's rows count.

The user is the one ASP.NET Core authenticated for the call: configure authentication on the server
(`AddAuthentication`, `UseAuthentication`), and send credentials from the client.

## Graphs

A graph saved remotely is checked as its parent: the policy and the validation apply to the parent. A child sent with a key
that belongs to another parent is rejected, so a client cannot take over rows through a graph.

## Notification hubs

Hubs broadcast keys of changed rows. Protect them like the data: `[Authorize]` on the MagicOnion hub class,
`.RequireAuthorization()` on the SignalR `MapHub`.

## Secrets

Keep connection strings and certificate passwords out of the source code: user secrets in development, environment
variables or a vault in production. Never commit them, and change any that was.
