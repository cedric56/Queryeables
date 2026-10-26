namespace Queryeables.Abstraction;

/// <summary>
/// Nom sous lequel le type d'entité est échangé entre client et serveur.
/// Par défaut, c'est son nom complet (namespace compris) ; cet attribut permet un nom plus court ou stable
/// même si la classe change de namespace.
/// </summary>
/// <example>
/// <code>
/// [RemoteName("Client")]
/// public class Client { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RemoteNameAttribute(string name) : Attribute
{
    public string Name { get; } = string.IsNullOrWhiteSpace(name)
        ? throw new ArgumentException("Le nom ne peut pas être vide.", nameof(name))
        : name;
}
