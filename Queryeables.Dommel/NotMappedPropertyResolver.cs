using Dommel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;

namespace Queryeables.Dommel;

/// <summary>
/// Fait ignorer à Dommel les propriétés marquées [NotMapped], comme le fait EF.
/// Activé par <see cref="ServiceCollectionExtensions.UseFactory{TDbConnection}"/>,
/// ou à la main avec <see cref="Register"/>.
/// </summary>
public sealed class NotMappedPropertyResolver : DefaultPropertyResolver
{
    private static int _registered;

    public override IEnumerable<ColumnPropertyInfo> ResolveProperties(Type type)
        => base.ResolveProperties(type)
            .Where(p => p.Property.GetCustomAttribute<NotMappedAttribute>() is null);

    /// <summary>Enregistre le résolveur auprès de Dommel (sans effet s'il l'est déjà).</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
            DommelMapper.SetPropertyResolver(new NotMappedPropertyResolver());
    }
}
