namespace Queryeables.EntityFramework;

/// <summary>
/// EF Core builds its model and its queries at run time, and annotates its API accordingly. The EF store says so to its
/// callers instead of hiding it: an application published trimmed or with Native AOT is warned where it uses it.
/// </summary>
internal static class EfAot
{
    public const string Trimming =
        "EF Core is not fully compatible with trimming: its model and queries use reflection. See https://aka.ms/efcore-docs-trimming.";

    public const string DynamicCode =
        "EF Core generates code at run time and is not compatible with Native AOT. Use a remote store (UseMagicOnion) in a Native AOT application.";
}
