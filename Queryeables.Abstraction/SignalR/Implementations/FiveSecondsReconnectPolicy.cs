using Microsoft.AspNetCore.SignalR.Client;
using System;

namespace Queryeables.Abstraction;

public class FiveSecondsReconnectPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        return TimeSpan.FromSeconds(5);
    }
}
