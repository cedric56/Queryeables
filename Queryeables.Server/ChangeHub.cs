using MagicOnion.Server.Hubs;
using Queryeables.Abstraction;
using Queryeables.Onion;

namespace Queryeables.Server;

/// <summary>
/// MagicOnion hub that broadcasts the changes of one entity type to subscribed clients,
/// through the shared <see cref="ChangeHubConnections{TObject}"/>.
/// </summary>
public class ChangeHub<TObject>(ChangeHubConnections<TObject> repository) :
    StreamingHubBase<IChangeHub<TObject>, IChangeHubReceiver<TObject>>,
    IChangeHub<TObject> where TObject : class
{
    /// <summary>One broadcast group per entity type.</summary>
    private static readonly string GroupName = "queryeables:" + typeof(TObject).FullName;

    /// <summary>Subscribes the connection to notifications and returns its id, to keep as the client-side session.</summary>
    public async Task<Guid> AddReceiverAsync()
    {
        var group = await Group.AddAsync(GroupName);
        repository.RegisterGroup(group, Context.ContextId);
        return Context.ContextId;
    }

    public Task RemoveAsync()
    {
        repository.Unregister(Context.ContextId);
        return Task.CompletedTask;
    }

    /// <summary>A client reports a change: it is relayed to the other clients.</summary>
    public void OnChangeReceived(ChangeMessage e)
    {
        var record = e.ToChange<TObject>();

        // The sender is always excluded, even if it did not add itself.
        var self = Context.ContextId.ToString();
        if (!record.Contains(self))
            record.Add(self);

        repository.Raise(record);
    }

    protected override ValueTask OnDisconnected()
    {
        repository.Unregister(Context.ContextId);
        return ValueTask.CompletedTask;
    }
}
