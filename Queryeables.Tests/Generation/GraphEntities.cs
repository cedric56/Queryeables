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

/// <summary>
/// Every relation an application of management meets: lines with their own taxes (two levels), one note (one to one), tags
/// through a link table with a composite key (many to many), a client (a reference held by a foreign key).
/// </summary>
public class GInvoice
{
    public long Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public long? GClientId { get; set; }
    public int Year { get; set; }

    [EntityReference]
    public GClient? GClient { get; set; }

    [EntityGraph]
    public List<GInvoiceLine>? Lines { get; set; }

    [EntityGraph(NullDeletes = true)]
    public GInvoiceNote? Note { get; set; }

    [EntityGraph(Through = typeof(GInvoiceTag))]
    public List<GTag>? Tags { get; set; }
}

public class GInvoiceLine
{
    public long Id { get; set; }
    public long GInvoiceId { get; set; }
    public string Product { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal Price { get; set; }

    [System.ComponentModel.DataAnnotations.ConcurrencyCheck]
    public long Version { get; set; }

    [EntityGraph]
    public List<GLineTax>? Taxes { get; set; }
}

public class GLineTax
{
    public long Id { get; set; }
    public long GInvoiceLineId { get; set; }
    public decimal Rate { get; set; }
}

public class GInvoiceNote
{
    public long Id { get; set; }
    public long GInvoiceId { get; set; }
    public string Text { get; set; } = string.Empty;
}

public class GTag
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>A link with a composite key: no key of its own.</summary>
public class GInvoiceTag
{
    [System.ComponentModel.DataAnnotations.Key]
    public long GInvoiceId { get; set; }

    [System.ComponentModel.DataAnnotations.Key]
    public long GTagId { get; set; }
}

public class GClient
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
}

/// <summary>A tree: tasks and their subtasks, of the same type, at any depth.</summary>
public class GTask
{
    public long Id { get; set; }
    public long? ParentId { get; set; }
    public string Title { get; set; } = string.Empty;

    [EntityGraph(ForeignKey = nameof(ParentId))]
    public List<GTask>? SubTasks { get; set; }
}
