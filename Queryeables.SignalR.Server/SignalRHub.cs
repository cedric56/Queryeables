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

    /// <summary>Un client signale un changement : il est relayé aux autres clients.</summary>
    [HubMethodName(SignalRContract.OnRecordChanged)]
    public void OnRecordChanged(Record<TObject> record)
    {
        // L'émetteur est toujours exclu, même s'il ne s'est pas ajouté lui-même.
        if (!record.Contains(Context.ConnectionId))
            record.Add(Context.ConnectionId);

        tracker.Relay(record);
    }
}
