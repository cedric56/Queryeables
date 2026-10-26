namespace Queryeables.SignalR;

/// <summary>Noms des messages échangés entre le client et le hub. Partagés par les deux côtés.</summary>
public static class SignalRContract
{
    /// <summary>Message du serveur vers les clients : une ligne a changé.</summary>
    public const string DbChanged = "DBChanged";

    /// <summary>Méthode du hub appelée par un client pour signaler un changement local.</summary>
    public const string OnRecordChanged = "OnRecordChanged";
}
