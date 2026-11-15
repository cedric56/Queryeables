using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Queryeables.MagicOnion.Server;
using Queryeables.Remote;

// The namespace of ASP.NET Core's Map* methods: found in Program.cs without a using.
namespace Microsoft.AspNetCore.Builder;

public static class QueryeablesEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the Queryeables service (<see cref="QueryeablesService"/>): what the clients of <c>UseMagicOnion</c> call. The
    /// change hubs of the entities, when there are any, are mapped by <c>app.MapMagicOnionService()</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// An exposed type cannot be served with the server's serializer (a property hidden by an attribute it ignores, such as
    /// <c>[IgnoreMember]</c> with JSON): said at startup rather than at the first request.
    /// </exception>
    public static IEndpointConventionBuilder MapQueryeables(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.ServiceProvider.GetService<EntityRequestDispatcher>()?.CheckConfiguration();
        return endpoints.MapMagicOnionService<QueryeablesService>();
    }
}
