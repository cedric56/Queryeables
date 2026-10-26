using Avae.DAL;
using Avae.Onion;
using MagicOnion.Server.Hubs;
using System;
using System.Threading.Tasks;

namespace Avae.Server;

/// <summary>
/// Streaming hub that broadcasts record change notifications for entities of type <typeparamref name="TObject"/>
/// to connected receivers, backed by a shared <see cref="RecordHubRepository{TObject}"/>.
/// </summary>
/// <typeparam name="TObject">The entity type this hub streams change notifications for.</typeparam>
public class RecordHub<TObject> :
 StreamingHubBase<IRecordHub<TObject>, IRecordHubReceiver<TObject>>,
 IRecordHub<TObject> where TObject : class, new()
{
    /// <summary>
    /// The shared repository used to register/unregister connections and dispatch change notifications.
    /// </summary>
    readonly RecordHubRepository<TObject> repository;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordHub{TObject}"/> class.
    /// </summary>
    /// <param name="repository">The repository used to manage receiver registration and dispatch notifications.</param>
    public RecordHub(RecordHubRepository<TObject> repository)
    {
        this.repository = repository;
    }

    /// <summary>
    /// Adds the current connection to the "customers" broadcast group and registers it with the
    /// repository so it receives future record change notifications.
    /// </summary>
    /// <returns>The current connection's context identifier.</returns>
    public async Task<Guid> AddReceiverAsync()
    {
        var group = await Group.AddAsync("customers");
        repository.RegisterGroup(group, this.Context.ContextId);
        return this.Context.ContextId;
    }

    /// <summary>
    /// Unregisters the current connection from the repository, stopping further record change notifications.
    /// </summary>
    /// <returns>A completed task.</returns>
    public Task RemoveAsync()
    {
        repository.Unregister(this.Context.ContextId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Forwards a record change event to the repository for broadcast to registered receivers.
    /// </summary>
    /// <param name="e">The record change event to broadcast.</param>
    public void OnRecordChanged(Record<TObject> e)
    {
        repository.Raise(e);
    }
}