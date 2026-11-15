using MagicOnion;
using Queryeables.Remote;

namespace Queryeables.MagicOnion.Server;

/// <summary>
/// The Queryeables MagicOnion service, mapped by <c>app.MapQueryeables()</c>. Left out of MagicOnion's discovery
/// ([Ignore]): an application that declares its own service class (deriving from <see cref="MagicOnionService"/>) and maps
/// it with <c>MapMagicOnionService()</c> keeps working.
/// </summary>
[Ignore]
public sealed class QueryeablesService(EntityRequestDispatcher dispatcher) : MagicOnionService(dispatcher);
