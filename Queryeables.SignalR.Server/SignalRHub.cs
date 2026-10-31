using Microsoft.AspNetCore.SignalR;
using Queryeables.Abstraction;

namespace Queryeables.SignalR.Server;

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
        tracker.Add(Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        tracker.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>A client reports a change: it is relayed to the other clients.</summary>
    [HubMethodName(SignalRContract.OnChangeReceived)]
    public void OnChangeReceived(SignalRChange message)
    {
        var record = message.ToChange<TObject>();

        // The sender is always excluded, even if it did not add itself.
        if (!record.Contains(Context.ConnectionId))
            record.Add(Context.ConnectionId);

        tracker.Relay(record);
    }
}
