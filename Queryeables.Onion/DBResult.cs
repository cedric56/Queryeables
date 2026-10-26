using MessagePack;
using Queryeables.Abstraction;

namespace Queryeables.Onion;

/// <summary>Réponse du serveur : données en cas de succès, nature et message de l'erreur sinon.</summary>
[MessagePackObject]
public partial class DBResult
{
    [Key(0)]
    public string? Exception { get; set; }

    [Key(1)]
    public bool Successful { get; set; }

    [Key(2)]
    public byte[]? Data { get; set; }

    [Key(3)]
    public EntityErrorCode ErrorCode { get; set; }

    /// <summary>Messages de validation, un par règle non respectée.</summary>
    [Key(4)]
    public string[]? Errors { get; set; }

    public static DBResult Success(byte[]? data = null) => new() { Successful = true, Data = data };

    public static DBResult Failure(EntityErrorCode errorCode, string message, IReadOnlyList<string>? errors = null)
        => new() { Successful = false, ErrorCode = errorCode, Exception = message, Errors = errors?.ToArray() };

    /// <summary>Convertit une exception côté serveur en réponse, avec la bonne nature d'erreur.</summary>
    public static DBResult FromException(Exception exception) => exception switch
    {
        EntityStoreException store => Failure(store.ErrorCode, store.Message, store.Errors),
        ArgumentException or NotSupportedException or FormatException
            => Failure(EntityErrorCode.InvalidRequest, exception.Message),
        _ => Failure(EntityErrorCode.Unknown,
            exception.InnerException is null ? exception.Message : $"{exception.Message}\n{exception.InnerException.Message}")
    };

    /// <summary>Exécute une opération serveur et met son résultat ou son erreur dans une réponse.</summary>
    public static async Task<DBResult> RunAsync(Func<Task<byte[]?>> action)
    {
        try
        {
            return Success(await action());
        }
        catch (Exception ex)
        {
            return FromException(ex);
        }
    }

    /// <summary>Côté client : renvoie les données, ou lève l'erreur du serveur sous forme d'<see cref="EntityStoreException"/>.</summary>
    public byte[]? GetDataOrThrow()
    {
        if (Successful)
            return Data;

        var code = ErrorCode == EntityErrorCode.None ? EntityErrorCode.Unknown : ErrorCode;
        throw new EntityStoreException(code, Exception ?? "L'appel distant a échoué.", Errors);
    }
}
