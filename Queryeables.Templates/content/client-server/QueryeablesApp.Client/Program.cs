using QueryeablesApp.Shared;

// The code of a local application; the provider is the server (started by: dotnet run --project QueryeablesApp.Server).
await using var app = await QueryeablesHost.CreateAsync(q => q
    .UseMagicOnion(args.FirstOrDefault() ?? "http://localhost:5070")
    .AddGeneratedEntities()
    .WaitForServer());

var persons = app.GetRequiredService<IPersonRepository>();
var ana = new Person { Name = $"Ana {DateTime.Now:HH:mm:ss}", Contacts = [new() { Kind = "friend" }, new() { Kind = "colleague" }] };
await persons.SaveAsync(ana).OrThrow();                 // the person and its contacts, in one transaction on the server

var everyone = await app.GetRequiredService<IEntityStoreFactory>().Query<Person>().OrderBy(p => p.Id).ToListAsync();
Console.WriteLine($"On the server: {string.Join(", ", everyone)}");

var loaded = await persons.GetAsync(ana.Id);
Console.WriteLine($"{loaded}: {string.Join(", ", loaded!.Contacts!.Select(c => c.Kind))}");
