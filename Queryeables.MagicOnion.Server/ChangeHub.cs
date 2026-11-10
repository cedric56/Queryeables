using Grpc.Core;
using MagicOnion.Server.Hubs;
using Queryeables.Abstraction;
using Queryeables.MagicOnion;

namespace Queryeables.MagicOnion.Server;

/// <summary>
/// MagicOnion hub that broadcasts the changes of one entity type to subscribed clients,
/// through the shared <see cref="ChangeHubConnections{TObject}"/>. Put <c>[Authorize]</c> on the class deriving from it
/// when the data is not public: the hub otherwise accepts any connection.
/// </summary>
public class ChangeHub<TObject>(ChangeHubConnections<TObject> repository) :
    StreamingHubBase<IChangeHub<TObject>, IChangeHubReceiver<TObject>>,
    IChangeHub<TObject> where TObject : class
{
    /// <summary>One broadcast group per entity type.</summary>
    private static readonly string GroupName = "queryeables:" + typeof(TObject).FullName;

    private IGroup<IChangeHubReceiver<TObject>>? _group;

    /// <summary>Subscribes the connection to notifications and returns its id, to keep as the client-side session.</summary>
    public async Task<Guid> AddReceiverAsync()
    {
        _group ??= await Group.AddAsync(GroupName);
        repository.RegisterGroup(_group, Context.ContextId, Context.CallContext.GetHttpContext().User);
        return Context.ContextId;
    }

    /// <summary>Unsubscribes the connection: it leaves the group, and receives nothing more.</summary>
    public async Task RemoveAsync()
    {
        repository.Unregister(Context.ContextId);
        if (Interlocked.Exchange(ref _group, null) is { } group)
            await group.RemoveAsync(Context);
    }

    /// <summary>
    /// A client reports a change: relayed to the other clients only when the server allows it
    /// (<see cref="ChangeHubOptions.RelayClientChanges"/>), checked and limited; otherwise ignored.
    /// </summary>
    public void OnChangeReceived(ChangeMessage e)
    {
        ArgumentNullException.ThrowIfNull(e);
        repository.Raise(e.ToChange<TObject>(), Context.ContextId);
    }

    protected override ValueTask OnDisconnected()
    {
        repository.Unregister(Context.ContextId);
        return ValueTask.CompletedTask;
    }
}
