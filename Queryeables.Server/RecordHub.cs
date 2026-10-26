using MagicOnion.Server.Hubs;
using Queryeables.Abstraction;
using Queryeables.Onion;

namespace Queryeables.Server;

/// <summary>
/// Hub MagicOnion qui diffuse aux clients abonnés les changements d'un type d'entité,
/// via le <see cref="RecordHubRepository{TObject}"/> partagé.
/// </summary>
public class RecordHub<TObject>(RecordHubRepository<TObject> repository) :
    StreamingHubBase<IRecordHub<TObject>, IRecordHubReceiver<TObject>>,
    IRecordHub<TObject> where TObject : class
{
    /// <summary>Abonne la connexion aux notifications et renvoie son identifiant, à garder comme session côté client.</summary>
    public async Task<Guid> AddReceiverAsync()
    {
        var group = await Group.AddAsync("customers");
        repository.RegisterGroup(group, Context.ContextId);
        return Context.ContextId;
    }

    public Task RemoveAsync()
    {
        repository.Unregister(Context.ContextId);
        return Task.CompletedTask;
    }

    /// <summary>Un client signale un changement : il est relayé aux autres clients.</summary>
    public void OnRecordChanged(Record<TObject> e)
    {
        // L'émetteur est toujours exclu, même s'il ne s'est pas ajouté lui-même.
        var self = Context.ContextId.ToString();
        if (!e.Contains(self))
            e.Add(self);

        repository.Raise(e);
    }

    protected override ValueTask OnDisconnected()
    {
        repository.Unregister(Context.ContextId);
        return ValueTask.CompletedTask;
    }
}
