namespace Queryeables.Abstraction;

/// <summary>
/// Name under which the entity type is exchanged between client and server.
/// Defaults to its full name (namespace included); this attribute allows a shorter or stable name
/// even if the class changes namespace.
/// </summary>
/// <example>
/// <code>
/// [RemoteName("Customer")]
/// public class Customer { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RemoteNameAttribute(string name) : Attribute
{
    public string Name { get; } = string.IsNullOrWhiteSpace(name)
        ? throw new ArgumentException("The name cannot be empty.", nameof(name))
        : name;
}
