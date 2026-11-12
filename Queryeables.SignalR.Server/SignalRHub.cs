using Microsoft.AspNetCore.SignalR;
using Queryeables.Abstraction;

namespace Queryeables.SignalR.Server;

/// <summary>
/// SignalR hub that broadcasts the changes of one entity type. Closed to anonymous users by default: authenticate its
/// endpoint (<c>.RequireAuthorization()</c>), or set <see cref="ChangeHubOptions.AllowAnonymous"/> for public data.
/// </summary>
/// <example>
/// <code>
/// builder.Services.AddSignalR().AddMessagePackProtocol();
/// builder.Services.AddSingleton&lt;ConnectionTracker&lt;Client&gt;&gt;();
/// app.MapHub&lt;SignalRHub&lt;Client&gt;&gt;("/hubs/client");
/// </code>
/// </example>
public class SignalRHub<TObject>(ConnectionTracker<TObject> tracker) : Hub where TObject : class
{
    public override Task OnConnectedAsync()
    {
        if (!tracker.Admits(Context.User))
            throw new HubException("Authentication required.");      // the connection is refused
        tracker.Add(Context.ConnectionId, Context.User);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        tracker.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// A client reports a change: relayed to the other clients only when the server allows it
    /// (<see cref="ChangeHubOptions.RelayClientChanges"/>), checked and limited; otherwise ignored.
    /// </summary>
    [HubMethodName(SignalRContract.OnChangeReceived)]
    public void OnChangeReceived(SignalRChange message)
    {
        ArgumentNullException.ThrowIfNull(message);
        tracker.RelayFromClient(message.ToChange<TObject>(), Context.ConnectionId);
    }
}
