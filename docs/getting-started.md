# Getting started

A console or desktop application with SQLite, a cache and a parent with its children. The whole application is the
`queryeables-console` template, built and run by the CI from the packages; the [tutorial](tutorial.md) goes further, up
to a server and offline work.

## 1. Add the package

```bash
dotnet add package Queryeables.Sqlite
dotnet add package Microsoft.Extensions.DependencyInjection
```

`Queryeables.Sqlite` brings the `Queryeables` package and its source generator. Or start from the template, which holds
everything below:

```bash
dotnet new install Queryeables.Templates
dotnet new queryeables-console -n MyApp
cd MyApp && dotnet run
```

### Before the packages are published

The packages are not on NuGet yet. Build them once from the sources, into a folder that serves as a package source:

```bash
git clone https://github.com/cedric56/Queryeables
cd Queryeables
dotnet pack -c Release -o ~/queryeables-packages -p:Version=0.1.0-local
dotnet nuget add source ~/queryeables-packages --name queryeables-local
```

(In PowerShell, `$HOME\queryeables-packages`.) Then, in your application, add `--prerelease`, since `0.1.0-local` is a
preview version:

```bash
dotnet add package Queryeables.Sqlite --prerelease
```

or install the templates from that folder:

```bash
dotnet new install ~/queryeables-packages/Queryeables.Templates.0.1.0-local.nupkg
dotnet new queryeables-console -n MyApp
```

After a `git pull`, pack again with a higher version (`0.1.0-local.2`): NuGet keeps a version it has already seen in its
cache. Once the packages are published, `dotnet nuget remove source queryeables-local`.

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

// The tables: Queryeables does not create them (use your migrations; this is the simplest way for a first app).
await DommelEntityStore.EnsureSchemaAsync(provider.GetRequiredService<DbProviderFactory>(), "Data Source=app.db", """
    CREATE TABLE IF NOT EXISTS Person  (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS Contact (Id INTEGER PRIMARY KEY AUTOINCREMENT, PersonId INTEGER NOT NULL, Kind TEXT NOT NULL);
    """);
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

`dotnet new queryeables-client-server -n Shop` makes the three projects below; the [tutorial](tutorial.md#4-go-remote)
walks through them. Keep the entities in a shared library, then:

- **server** (ASP.NET Core): `Queryeables.MagicOnion.Server`, `q.UseSqlite(...).AddGeneratedEntities().AddMagicOnionServer(s => s.Expose<Person>(policy).Expose<Contact>(policy))`;
- **client**: `Queryeables.MagicOnion.Client`, `q.UseMagicOnion("https://server:5001").AddGeneratedEntities()`.

The client code does not change. See [Remote access](guides/remote-access.md).
