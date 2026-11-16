using MagicOnion;

namespace QueryeablesApp.Shared;

/// <summary>
/// The writes of the client: a service of the application, run on the server with its own stores, under its own rules. The
/// client reads the persons from its replica (IPersonCache), which follows the server's changes.
/// </summary>
public interface IPeopleService : IService<IPeopleService>
{
    /// <summary>Adds a person with its contacts, in one transaction; returns its id.</summary>
    UnaryResult<long> AddAsync(string name, string[] contactKinds);
}
