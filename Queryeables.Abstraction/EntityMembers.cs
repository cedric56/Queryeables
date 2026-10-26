using System.Diagnostics.CodeAnalysis;

namespace Queryeables.Abstraction;

/// <summary>
/// Members of entity types that Queryeables reads through reflection (keys, versions, [LastModified], [SoftDelete],
/// filters, validation). Generic parameters that carry entities are annotated with
/// <c>[DynamicallyAccessedMembers(EntityMembers.Required)]</c> so that trimming and Native AOT keep them.
/// </summary>
public static class EntityMembers
{
    public const DynamicallyAccessedMemberTypes Required = DynamicallyAccessedMemberTypes.PublicProperties;
}
