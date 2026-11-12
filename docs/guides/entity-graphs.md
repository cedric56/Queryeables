# Entity graphs

An entity graph is a parent and its child collections, saved, removed and loaded together.

## Declare it

The attributes describe every relation; the generator writes the graph from them.

```csharp
public class Invoice
{
    public long Id { get; set; }
    public long? ClientId { get; set; }

    [EntityReference]                                      // a navigation held by a foreign key of the invoice
    public Client? Client { get; set; }

    [EntityGraph]                                          // one to many: InvoiceId (or IdInvoice) on the line
    public List<InvoiceLine>? Lines { get; set; }

    [EntityGraph(NullDeletes = true)]                      // one to one: a row of another table holding InvoiceId
    public InvoiceNote? Note { get; set; }

    [EntityGraph(Through = typeof(InvoiceTag))]            // many to many, through a table of links
    public List<Tag>? Tags { get; set; }
}

public class InvoiceLine
{
    public long Id { get; set; }
    public long InvoiceId { get; set; }

    [EntityGraph]                                          // a second level: saved, removed and loaded with the invoice
    public List<LineTax>? Taxes { get; set; }
}

public class InvoiceTag                                    // a composite key: no key of its own
{
    [Key] public long InvoiceId { get; set; }
    [Key] public long TagId { get; set; }
}
```

| Relation | Attribute | Saved with the parent | Filter through it |
| --- | --- | --- | --- |
| One to many | `[EntityGraph]` on a collection | The children: inserted, updated when changed, deleted when gone | `i.Lines.Any(l => ...)`, `All`, `Count(...) > n` |
| One to one | `[EntityGraph]` on an entity | The child (`NullDeletes`: null deletes it; otherwise null means "not loaded") | `i.Note.Text == "..."`, `i.Note == null` |
| Many to many | `[EntityGraph(Through = typeof(Link))]` | The links only, never the linked entities | `i.Tags.Any(t => ...)` |
| Reference | `[EntityReference]` on an entity | Nothing (only its foreign key, a column of the parent) | `i.Client.City == "Paris"`, `i.Client == null` |

The other attribute properties name what the conventions do not find: `ForeignKey`, `ParentNavigation`, `LookupNavigation`,
`LookupForeignKey` on `[EntityGraph]`, `ThroughForeignKey` and `ThroughTargetKey` for a link, `ForeignKey` on
`[EntityReference]`.

**Levels.** A child type with `[EntityGraph]` relations of its own is saved, removed and loaded with them: a graph has as
many levels as its types. A type whose children are of the same type (a task and its subtasks) is a tree, of any depth.

**Composite keys.** An entity may have several `[Key]` properties (a link table): its key is then all of them, and a row
with a key not stored yet is a new one. The stores read, write and delete it by every part.

The generator writes, for each parent:

- `IInvoiceRepository` / `InvoiceRepository` (also registered as `IAggregateRepository<Invoice>`): `SaveAsync`, `RemoveAsync`,
  `LoadChildrenAsync` (every relation, for one entity or a whole list) and `LoadLinesAsync` (this relation only);
- `IInvoiceGraph` / `InvoiceGraph`: sets the parent and lookup navigations of the loaded children; it receives the caches
  of the looked-up types. The graph is also registered for every store, so that filters through its relations run anywhere.

Both classes are `partial`: add your own methods next to the generated ones.

## Save

`SaveAsync(invoice)`, in one transaction:

1. inserts the invoice (its key is generated) or updates it;
2. writes its key into the foreign key of every child;
3. for each relation, deletes the stored children that are no longer there (their own children first), inserts the new
   ones (key 0, or a composite key not stored yet), and updates only those that changed; then the same for their children.

| The collection is | Saving |
| --- | --- |
| `null` | Leaves the stored children as they are: they were not loaded |
| empty | Deletes every stored child |
| a list | Aligns the stored children with it |

> [!WARNING]
> Do not initialize the collection (`= []`). An entity read from a cache or a query would then have an empty list, and
> saving it would delete its children.

**Only what changed.** A child whose values are those of its stored row is not written: no update, no notification. A
child that still has the values it was loaded with (`LoadChildrenAsync` remembers them, `EntityOriginals`) is left alone
even when its row changed meanwhile: two users editing different lines of one invoice do not conflict, each line keeps the
change of the user who made it. A line both changed is a conflict (with a `[ConcurrencyCheck]` version), as before.

A child with a key that belongs to another parent is rejected (`Conflict`): a client cannot move or overwrite another
parent's rows. A child that vanished meanwhile fails the transaction (`NotFound`).

## Remove and load

`RemoveAsync(invoice)` deletes the children read from the database at every level (the links of a many to many, never the
linked entities), then the parent.

`LoadChildrenAsync(invoices)` reads the relations of a whole list with **one query per relation and per level**, by groups
of keys (`key in (...)`): 50 invoices with their lines and their taxes cost 2 queries, not 101. The references are set the
same way (one query for every client of the list).

## Filters through the relations

Every store turns a filter that goes through a relation into queries on columns, from the graph of the type:

```csharp
var withTea = await store.SelectAsync<Invoice>(i => i.Lines!.Any(l => l.Product == "tea"));
var empty = await store.SelectAsync<Invoice>(i => !i.Lines!.Any());
var big = await store.SelectAsync<Invoice>(i => i.Lines!.Count() >= 10 && i.Client!.City == "Paris");
var urgent = await store.SelectAsync<Invoice>(i => i.Tags!.Any(t => t.Name == "urgent"));
```

The related rows matching the inner condition are read first, then their keys become `key in (...)` on the parent: at most
`EntityRelations.MaxKeys` (2000 by default, under the 2100 parameters of SQL Server). A condition reads one navigation at a
time (`i.Client.Name == i.Number` is refused). Remotely, the related type must be exposed and readable by the user, and
only its rows the user may read count. In the offline copy, the same filters run on the copied rows.

## Selecting children in a form

`CollectionExtensions.Update` aligns a collection with a selection in place, without replacing the items that stay:

```csharp
person.Contacts ??= [];
person.Contacts.Update(selectedPersons,
    (selected, contact) => contact.IdPerson == selected.Id,
    selected => new Contact { IdPerson = selected.Id, Person = selected });
await personService.SaveAsync(person);
```

Load the contacts first (`LoadContactsAsync`): the selection is the complete list, and contacts not selected are deleted.

## Remotely

With a remote provider, `SaveAsync` sends the parent with its children in one call, with the ids of the children the user
left as loaded; the server saves them with its own `IInvoiceRepository` and leaves those rows alone. The server must
therefore register the graph: `AddGeneratedEntities()` on the server, with the entities in a library both sides reference.
`Expose<Invoice>` fails at startup otherwise.

## Without the generator

```csharp
q.AddGraph<Person, long>(sp => EntityGraph.For<Person, long>(p => p.Id)
    .HasMany(p => p.Contacts, foreignKey: c => c.PersonId, key: c => c.Id,
             onLoaded: (person, contacts) => { foreach (var c in contacts) c.Owner = person; }));
```

`HasOne`, `HasManyThrough` and `References` declare the other relations; the `graph` argument of `HasMany` and `HasOne`
gives a child type its own children. A graph built by hand is known to the stores once registered (`AddGraph`, a
`GraphRepository`, or `EntityRelations.Register`).

The generator does not nest a child type whose graph leads back to its parent through other types (A → B → A): save B
through its own repository there.
