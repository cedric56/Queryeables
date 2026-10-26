using Microsoft.AspNetCore.SignalR.Client;

namespace Queryeables.SignalR;

/// <summary>Reconnexion toutes les 5 secondes, sans limite.</summary>
public sealed class FiveSecondsReconnectPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext) => TimeSpan.FromSeconds(5);
}
