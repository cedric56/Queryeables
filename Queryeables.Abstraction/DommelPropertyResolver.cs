using Dommel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;

namespace Example.Models;

internal class DommelPropertyResolver : DefaultPropertyResolver
{
    public override IEnumerable<ColumnPropertyInfo> ResolveProperties(Type type)
    {
        var properties = base.ResolveProperties(type);
        foreach (var propertyInfo in properties)
        {
            var notMappedAttr = propertyInfo.Property.GetCustomAttribute<NotMappedAttribute>();

            if (notMappedAttr == null)
            {
                yield return propertyInfo;
            }
        }
    }
}
