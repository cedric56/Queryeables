namespace Queryeables.Abstraction;

/// <summary>Nature d'un échec, identique en local et à distance.</summary>
public enum EntityErrorCode
{
    None = 0,

    /// <summary>Erreur inattendue (base indisponible, bug...).</summary>
    Unknown = 1,

    /// <summary>La ligne visée n'existe pas (dans un lot, une mise à jour ou suppression sans effet).</summary>
    NotFound = 2,

    /// <summary>L'utilisateur n'a pas le droit de faire cette opération.</summary>
    Forbidden = 3,

    /// <summary>L'entité ne respecte pas ses règles de validation (voir <see cref="EntityStoreException.Errors"/>).</summary>
    Validation = 4,

    /// <summary>La ligne a été modifiée entre-temps par quelqu'un d'autre (concurrence optimiste).</summary>
    Conflict = 5,

    /// <summary>Le type d'entité n'est pas exposé par le serveur.</summary>
    UnknownType = 6,

    /// <summary>Opération non prise en charge par ce store ou ce serveur.</summary>
    NotSupported = 7,

    /// <summary>Requête mal formée (filtre invalide, page 0...).</summary>
    InvalidRequest = 8,
}

/// <summary>Échec d'une opération de store, avec sa nature pour que l'appelant puisse réagir.</summary>
public class EntityStoreException : Exception
{
    public EntityStoreException(EntityErrorCode errorCode, string message,
        IReadOnlyList<string>? errors = null, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        Errors = errors ?? [];
    }

    public EntityErrorCode ErrorCode { get; }

    /// <summary>Messages de validation, un par règle non respectée.</summary>
    public IReadOnlyList<string> Errors { get; }
}
