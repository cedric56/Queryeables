using Microsoft.AspNetCore.SignalR;
using System;
using System.Threading.Tasks;

namespace Queryeables.Abstraction;

public class SignalRHub<TObject> : Hub where TObject : class, new()
{
    readonly ConnectionTracker<TObject> tracker;

    public SignalRHub(ConnectionTracker<TObject> tracker)
    {
        this.tracker = tracker; // no monitor subscription here anymore
    }

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

    public void OnRecordChanged(Record<TObject> record)
    {
        tracker.OnRecordChanged(this, record);
    }
}
