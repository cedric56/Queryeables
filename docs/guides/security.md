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
