using Microsoft.AspNetCore.SignalR;
using Queryeables.Abstraction;

namespace Queryeables.SignalR.Server;

/// <summary>
/// SignalR hub that broadcasts the changes of one entity type. Map it with <c>.RequireAuthorization()</c> when the data is
/// not public: the hub otherwise accepts any connection.
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
