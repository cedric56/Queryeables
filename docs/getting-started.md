# Getting started

A console or desktop application with SQLite, a cache and a parent with its children. The complete code is the
[sample](https://github.com/cedric56/Queryeables/tree/master/Samples/Queryeables.Sample), run by the CI.

## 1. Add the package

```bash
dotnet add package Queryeables.Sqlite
dotnet add package Microsoft.Extensions.DependencyInjection
```

`Queryeables.Sqlite` brings the `Queryeables` package and its source generator.

## 2. Describe the entities

```csharp
[EntityCache]
[Table("Person")]                     // Dommel pluralizes table names by default
public class Person
{
    public long Id { get; set; }
    public string Name { get; set; } = "";

    [NotMapped]                       // not a column
    [EntityGraph(ParentNavigation = nameof(Contact.Owner))]
    public List<Contact>? Contacts { get; set; }   // null = not loaded
}

[Table("Contact")]
public class Contact
{
    public long Id { get; set; }
    public long PersonId { get; set; }             // found by its name: {Parent}Id or Id{Parent}
    public string Kind { get; set; } = "";

    [NotMapped] public Person? Owner { get; set; }
}
```

## 3. Register

```csharp
var services = new ServiceCollection();
services.UseQueryeables(q => q
    .UseSqlite("Data Source=app.db")
    .AddGeneratedEntities());
await using var provider = services.BuildServiceProvider();
```

## 4. Use

```csharp
var cache = provider.GetRequiredService<IEntityCache<Person, long>>();
await cache.LoadAsync();

var persons = provider.GetRequiredService<IPersonRepository>();
var ana = new Person { Name = "Ana", Contacts = [new() { Kind = "friend" }] };
EntityResult result = await persons.SaveAsync(ana);     // the person, then its contacts, in one transaction

// A moment later, cache.Entities contains Ana: SQLite reported the insert.
```

## 5. Go remote

Keep the entities in a shared library, then:

- **server** (ASP.NET Core): `Queryeables.Server`, `q.UseSqlite(...).AddGeneratedEntities().AddMagicOnionServer(s => s.Expose<Person>(policy).Expose<Contact>(policy))`;
- **client**: `Queryeables.Onion.Client`, `q.UseMagicOnion("https://server:5001").AddGeneratedEntities()`.

The client code does not change. See [Remote access](guides/remote-access.md).
