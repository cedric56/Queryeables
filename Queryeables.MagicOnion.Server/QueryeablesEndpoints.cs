using Microsoft.AspNetCore.Routing;
using Queryeables.MagicOnion.Server;

// The namespace of ASP.NET Core's Map* methods: found in Program.cs without a using.
namespace Microsoft.AspNetCore.Builder;

public static class QueryeablesEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the Queryeables service (<see cref="QueryeablesService"/>): what the clients of <c>UseMagicOnion</c> call. The
    /// change hubs of the entities, when there are any, are mapped by <c>app.MapMagicOnionService()</c>.
    /// </summary>
    public static IEndpointConventionBuilder MapQueryeables(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapMagicOnionService<QueryeablesService>();
    }
}
