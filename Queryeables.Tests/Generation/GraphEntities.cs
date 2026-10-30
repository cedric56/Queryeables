using Queryeables.Abstraction;

namespace Queryeables.Tests.Graphs;

// Entities seen by the source generator, like in an application.

/// <summary>A person and its contacts; each contact points to another person (lookup of the same type).</summary>
[EntityCache]
public class GPerson
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;

    [EntityGraph(ForeignKey = nameof(GContact.OwnerId), ParentNavigation = nameof(GContact.Owner),
        LookupNavigation = nameof(GContact.Person), LookupForeignKey = nameof(GContact.PersonId))]
    public List<GContact> Contacts { get; set; } = [];
}

public class GContact
{
    public long Id { get; set; }
    public long OwnerId { get; set; }
    public long? PersonId { get; set; }
    public string Label { get; set; } = string.Empty;
    public GPerson? Owner { get; set; }
    public GPerson? Person { get; set; }
}

/// <summary>An order and its lines; each line points to a product (lookup of another type, found by convention).</summary>
public class GOrder
{
    public long Id { get; set; }

    [EntityGraph(ParentNavigation = nameof(GOrderLine.Order), LookupNavigation = nameof(GOrderLine.Product))]
    public List<GOrderLine> Lines { get; set; } = [];
}

public class GOrderLine
{
    public long Id { get; set; }
    public long GOrderId { get; set; }            // foreign key by convention: {Parent}Id
    public long ProductId { get; set; }           // lookup key by convention: {Navigation}Id
    public int Quantity { get; set; }
    public GOrder? Order { get; set; }
    public GProduct? Product { get; set; }
}

/// <summary>No [EntityCache]: its cache is registered because order lines look products up.</summary>
public class GProduct
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
