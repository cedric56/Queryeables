using QueryeablesApp.Shared;

// The code of a local application; the provider is the server (started by: dotnet run --project QueryeablesApp.Server).
await using var app = await QueryeablesHost.CreateAsync(q => q
    .UseMagicOnion(args.FirstOrDefault() ?? "http://localhost:5070")
    .UseRemoteWrites()                                  // Queryeables.Offline: writes through the server (AllowWrites there)
    .AddGeneratedEntities()
    .WaitForServer());

var persons = app.GetRequiredService<IPersonRepository>();
var ana = new Person { Name = $"Ana {DateTime.Now:HH:mm:ss}", Contacts = [new() { Kind = "friend" }, new() { Kind = "colleague" }] };
await persons.SaveAsync(ana).OrThrow();                 // the person and its contacts, in one transaction on the server

// The replica of the persons: what the server publishes, kept up to date by its notifications, queried in memory.
var replica = app.GetRequiredService<IPersonCache>();
await replica.LoadAsync();
Console.WriteLine($"On the server: {string.Join(", ", replica.Entities.OrderBy(p => p.Id))}");

var loaded = await persons.GetAsync(ana.Id);
Console.WriteLine($"{loaded}: {string.Join(", ", loaded!.Contacts!.Select(c => c.Kind))}");
