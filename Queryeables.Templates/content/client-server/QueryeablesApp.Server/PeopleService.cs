using MagicOnion;
using MagicOnion.Server;
using QueryeablesApp.Shared;

namespace QueryeablesApp.Server;

/// <summary>The writes of the clients: validated here, saved with the graph repository of the server.</summary>
public sealed class PeopleService(IPersonRepository persons) : ServiceBase<IPeopleService>, IPeopleService
{
    public async UnaryResult<long> AddAsync(string name, string[] contactKinds)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ReturnStatusException(Grpc.Core.StatusCode.InvalidArgument, "A person needs a name.");

        var person = new Person { Name = name.Trim(), Contacts = [.. contactKinds.Select(kind => new Contact { Kind = kind })] };
        await persons.SaveAsync(person).OrThrow();          // the person and its contacts, in one transaction
        return person.Id;
    }
}
