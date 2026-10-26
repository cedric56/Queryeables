using MagicOnion;

namespace Queryeables.Onion;

/// <summary>
/// Service distant d'accès aux entités. Le serveur l'implémente en déléguant à
/// <see cref="EntityRequestDispatcher"/> ; le client l'utilise via <see cref="MagicOnionEntityStore"/>.
/// <para>
/// Les filtres sont des conditions « propriété opérateur valeur » (<see cref="FilterCondition"/>),
/// combinées toutes en ET (<c>any</c> = false) ou toutes en OU (<c>any</c> = true).
/// Aucun SQL ne transite : seuls les types enregistrés côté serveur sont accessibles, selon leurs règles d'accès.
/// </para>
/// <para>En cas d'échec, <see cref="DBResult.ErrorCode"/> indique la nature de l'erreur.</para>
/// </summary>
public interface IMagicOnionLayer : IService<IMagicOnionLayer>
{
    UnaryResult<DBResult> GetAllAsync(string type);

    UnaryResult<DBResult> SelectAsync(string type, FilterCondition[] conditions, bool any);

    UnaryResult<DBResult> SelectPagedAsync(string type, FilterCondition[] conditions, bool any, int pageNumber, int pageSize);

    /// <summary>Renvoie l'entité insérée, avec sa clé générée et sa version.</summary>
    UnaryResult<DBResult> InsertAsync(string type, byte[] entity, string? connectionId);

    /// <summary>Renvoie l'entité mise à jour (nouvelle version), ou des données vides si la ligne n'existe pas.</summary>
    UnaryResult<DBResult> UpdateAsync(string type, byte[] entity, string? connectionId);

    /// <summary>Renvoie true si la ligne a été supprimée.</summary>
    UnaryResult<DBResult> DeleteAsync(string type, byte[] entity, string? connectionId);

    /// <summary>
    /// Exécute toutes les écritures dans une seule transaction : toutes réussissent, ou aucune n'est appliquée.
    /// Renvoie un résultat par opération (entité pour une insertion ou une mise à jour, true pour une suppression).
    /// </summary>
    UnaryResult<DBResult> ExecuteBatchAsync(BatchOperation[] operations);
}
