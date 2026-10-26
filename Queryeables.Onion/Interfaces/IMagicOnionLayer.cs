using MagicOnion;

namespace Queryeables.Onion;

/// <summary>
/// Service distant d'accès aux entités. Le serveur l'implémente en déléguant à
/// <see cref="EntityRequestDispatcher"/> ; le client l'utilise via <see cref="MagicOnionEntityStore"/>.
/// <para>
/// Les filtres sont des égalités propriété = valeur, combinées en ET (<c>any</c> = false) ou en OU (<c>any</c> = true).
/// Une liste de valeurs pour une propriété signifie « l'une de ces valeurs » (équivalent d'un IN).
/// Aucun SQL ne transite : seules les tables enregistrées côté serveur sont accessibles.
/// </para>
/// </summary>
public interface IMagicOnionLayer : IService<IMagicOnionLayer>
{
    UnaryResult<DBResult> GetAllAsync(string type);

    UnaryResult<DBResult> SelectAsync(string type, Dictionary<string, object?> filters, bool any);

    UnaryResult<DBResult> SelectPagedAsync(string type, Dictionary<string, object?> filters, bool any, int pageNumber, int pageSize);

    /// <summary>Renvoie l'entité insérée, avec sa clé générée.</summary>
    UnaryResult<DBResult> InsertAsync(string type, byte[] entity, string? connectionId);

    UnaryResult<DBResult> UpdateAsync(string type, byte[] entity, string? connectionId);

    UnaryResult<DBResult> DeleteAsync(string type, byte[] entity, string? connectionId);
}
