using Dommel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;

namespace Queryeables.Dommel;

/// <summary>
/// Fait ignorer à Dommel les propriétés marquées [NotMapped], comme le fait EF.
/// Il peut envelopper un résolveur existant, pour ne pas écraser un réglage personnalisé.
/// </summary>
/// <example>
/// <code>
/// // Seul :
/// DommelMapper.SetPropertyResolver(new NotMappedPropertyResolver());
/// // Avec votre propre résolveur :
/// DommelMapper.SetPropertyResolver(new NotMappedPropertyResolver(new MonResolveur()));
/// </code>
/// </example>
public sealed class NotMappedPropertyResolver(IPropertyResolver? inner = null) : DefaultPropertyResolver
{
    public override IEnumerable<ColumnPropertyInfo> ResolveProperties(Type type)
        => (inner?.ResolveProperties(type) ?? base.ResolveProperties(type))
            .Where(p => p.Property.GetCustomAttribute<NotMappedAttribute>() is null);
}
