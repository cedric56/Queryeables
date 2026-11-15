# Getting started

A console application with SQLite, a cache and a person with its contacts. It is the `queryeables-console` template
(`dotnet new queryeables-console -n MyApp`); the [tutorial](tutorial.md) goes on to a server and offline work.

```bash
dotnet add package Queryeables.Sqlite
```

```csharp
[EntityCache]                                                  // a cache that follows the table
public class Person
{
    public long Id { get; set; }
    public string Name { get; set; } = "";

    [EntityGraph]                                              // saved, read and removed with the person
    public List<Contact>? Contacts { get; set; }
}

public class Contact
{
    public long Id { get; set; }
    public long PersonId { get; set; }                         // the parent, found by its name
    public string Kind { get; set; } = "";
}

await using var app = await QueryeablesHost.CreateAsync(q => q
    .UseSqlite("Data Source=app.db")
    .AddGeneratedEntities()                                    // the cache, the repository: written by the generator
    .CreateTables());                                          // tables Person and Contact, when missing

var persons = app.GetRequiredService<IPersonRepository>();
await persons.SaveAsync(new Person { Name = "Ana", Contacts = [new() { Kind = "friend" }] }).OrThrow();
var cached = app.GetRequiredService<IPersonCache>().Entities;  // Ana is there already
```

No `using` to write: the package adds its namespaces to a project with `ImplicitUsings`. `CreateTables()` is for a first
application; manage the schema of a database in production with your migrations. The packages are not published on
NuGet yet: build them first ([contributing](../CONTRIBUTING.md#trying-the-packages-before-they-are-published)).

## Which package

| You have | Add | Then |
| --- | --- | --- |
| SQLite | `Queryeables.Sqlite` | `q.UseSqlite(connectionString)` |
| PostgreSQL | `Queryeables.PostgreSql` | `q.UseNpgsql(connectionString)` |
| SQL Server, MySQL | `Queryeables.Dommel` and the ADO.NET driver | `q.UseDommel<SqlConnection>(connectionString)` |
| An EF Core model | `Queryeables.EntityFramework` | `q.UseEntityFramework(sp => new AppDbContext(options))` |
| MongoDB | `Queryeables.MongoDB` | `q.UseMongoDB(connectionString, "app")` |
| A Queryeables server to reach | `Queryeables.MagicOnion.Client` | `q.UseMagicOnion("https://server:5001")` |
| A server to build | `Queryeables.MagicOnion.Server` and a provider | `.AddMagicOnionServer(s => s.ExposeGenerated(policy))`, `app.MapQueryeables()` |
| Entities shared by a client and a server | `Queryeables` in the shared library | the attributes, nothing else |

Three names cover the everyday work: the **repository** (`IPersonRepository`, a parent with its children), the **cache**
(`IPersonCache`) and the **query** (`stores.Query<Person>()`). The others are in the [glossary](glossary.md).
