# Entity graphs

An entity graph is a parent and its child collections, saved, removed and loaded together.

## Declare it

```csharp
public class Person
{
    public long Id { get; set; }

    [EntityGraph(ForeignKey = nameof(Contact.IdContact),          // child → parent; default {Parent}Id or Id{Parent}
                 ParentNavigation = nameof(Contact.Owner),         // set to the parent after loading
                 LookupNavigation = nameof(Contact.Person),        // another entity, found in its cache
                 LookupForeignKey = nameof(Contact.IdPerson))]     // default {Navigation}Id or Id{Navigation}
    public List<Contact>? Contacts { get; set; }
}
```

The generator writes, for each parent:

- `IPersonRepository` / `PersonRepository` (also registered as `IAggregateRepository<Person>`): `SaveAsync`, `RemoveAsync`,
  `LoadChildrenAsync` (every collection) and `LoadContactsAsync` (this collection only);
- `IPersonGraph` / `PersonGraph`: sets `Owner` and `Person` on the loaded contacts; it receives `IEntityCache<Person, long>`
  for the lookup.

Both classes are `partial`: add your own methods next to the generated ones.

## Save

`SaveAsync(person)`, in one transaction:

1. inserts the person (its key is generated) or updates it;
2. writes the person's key into the foreign key of every contact;
3. deletes the stored contacts that are no longer in the list, inserts the new ones (key 0), updates the others.

| The collection is | Saving |
| --- | --- |
| `null` | Leaves the stored children as they are: they were not loaded |
| empty | Deletes every stored child |
| a list | Aligns the stored children with it |

> [!WARNING]
> Do not initialize the collection (`= []`). An entity read from a cache or a query would then have an empty list, and
> saving it would delete its children.

A child with a key that belongs to another parent is rejected (`Conflict`): a client cannot move or overwrite another
parent's rows. A child that vanished meanwhile fails the transaction (`NotFound`).

## Remove and load

`RemoveAsync(person)` deletes the children read from the database, then the parent. `LoadContactsAsync(person)` reads the
contacts, sets `Contacts`, then calls the graph to set the navigation properties.

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

With a remote provider, `SaveAsync` sends the parent with its children in one call; the server saves them with its own
`IPersonRepository`. The server must therefore register the graph: `AddGeneratedEntities()` on the server, with the entities
in a library both sides reference. `Expose<Person>` fails at startup otherwise.

## Without the generator

```csharp
q.AddGraph<Person, long>(sp => EntityGraph.For<Person, long>(p => p.Id)
    .HasMany(p => p.Contacts, foreignKey: c => c.PersonId, key: c => c.Id,
             onLoaded: (person, contacts) => { foreach (var c in contacts) c.Owner = person; }));
```

Limits: one level of children per graph (a child's own children are not saved with it), and one key property per entity.
