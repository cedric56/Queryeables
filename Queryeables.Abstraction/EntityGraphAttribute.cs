using System;

namespace Queryeables.Abstraction;

[AttributeUsage(AttributeTargets.Property)]
public sealed class EntityGraphAttribute : Attribute
{
    public string? ParentNavigation { get; set; }

    public string? LookupNavigation { get; set; }

    // Clé étrangère utilisée par EntityGraph.HasMany.
    public string? ForeignKey { get; set; }

    // Clé étrangère utilisée pour retrouver la navigation LookupNavigation.
    public string? LookupForeignKey { get; set; }
}