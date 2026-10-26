using Dommel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;

namespace Queryeables.Dommel;

/// <summary>
/// Makes Dommel ignore properties marked [NotMapped], as EF does.
/// It can wrap an existing resolver, so as not to overwrite a custom setting.
/// </summary>
/// <example>
/// <code>
/// // Alone:
/// DommelMapper.SetPropertyResolver(new NotMappedPropertyResolver());
/// // With your own resolver:
/// DommelMapper.SetPropertyResolver(new NotMappedPropertyResolver(new MyResolver()));
/// </code>
/// </example>
public sealed class NotMappedPropertyResolver(IPropertyResolver? inner = null) : DefaultPropertyResolver
{
    public override IEnumerable<ColumnPropertyInfo> ResolveProperties(Type type)
        => (inner?.ResolveProperties(type) ?? base.ResolveProperties(type))
            .Where(p => p.Property.GetCustomAttribute<NotMappedAttribute>() is null);
}
