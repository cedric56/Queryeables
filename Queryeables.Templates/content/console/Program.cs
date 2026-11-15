using QueryeablesApp;

// SQLite, the tables created from Models.cs, and what its attributes describe: the cache, the graph and its repository.
await using var app = await QueryeablesHost.CreateAsync(q => q
    .UseSqlite($"Data Source={Path.Combine(AppContext.BaseDirectory, "app.db")}")
    .AddGeneratedEntities()
    .CreateTables());

var persons = app.GetRequiredService<IPersonRepository>();
var cache = app.GetRequiredService<IPersonCache>();

// A person and its contacts, saved in one transaction; in the cache as soon as the save returns.
var ana = new Person { Name = "Ana", Contacts = [new() { Kind = "friend" }, new() { Kind = "colleague" }] };
await persons.SaveAsync(ana).OrThrow();
Console.WriteLine($"In the cache: {string.Join(", ", cache.Entities.OrderBy(p => p.Id))}");

// Read again with its contacts, in one call; then removed, contacts first.
var loaded = await persons.GetAsync(ana.Id);
Console.WriteLine($"{loaded}: {string.Join(", ", loaded!.Contacts!.Select(c => c.Kind))}");

await persons.RemoveAsync(ana).OrThrow();
Console.WriteLine($"Removed: {cache.Find(ana.Id) is null}");
