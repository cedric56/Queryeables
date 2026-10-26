using Microsoft.AspNetCore.SignalR.Client;

namespace Queryeables.SignalR;

/// <summary>Reconnects every 5 seconds, without limit.</summary>
public sealed class FiveSecondsReconnectPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext) => TimeSpan.FromSeconds(5);
}
