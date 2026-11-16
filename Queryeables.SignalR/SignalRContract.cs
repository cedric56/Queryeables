namespace Queryeables.SignalR;

/// <summary>Names of the messages exchanged between the client and the hub. Shared by both sides.</summary>
public static class SignalRContract
{
    /// <summary>Message from the server to the clients: a row changed.</summary>
    public const string DbChanged = "DBChanged";
}
