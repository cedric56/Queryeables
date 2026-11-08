namespace Queryeables.MongoDB;

/// <summary>
/// The MongoDB driver maps classes and translates expressions with reflection, at run time. The MongoDB store says so to
/// its callers, as the EF Core store does: an application published trimmed or with Native AOT is warned where it uses it.
/// </summary>
internal static class MongoAot
{
    public const string Trimming =
        "The MongoDB driver maps classes and translates expressions with reflection: it is not compatible with trimming.";

    public const string DynamicCode =
        "The MongoDB driver generates code at run time and is not compatible with Native AOT. Use a remote store (UseMagicOnion) in a Native AOT application.";
}
