using Queryeables.MagicOnion.Client;
using QueryeablesApp.Shared;

// The provider is the server (started by: dotnet run --project QueryeablesApp.Server).
var url = args.FirstOrDefault() ?? "http://localhost:5070";
await using var app = await QueryeablesHost.CreateAsync(q => q
    .UseMagicOnion(url)
    .AddGeneratedEntities()
    .WaitForServer());

// A write: a service of the application, run on the server.
var people = app.Create<IPeopleService>(url);
var id = await people.AddAsync($"Ana {DateTime.Now:HH:mm:ss}", ["friend", "colleague"]);

// The replica of the persons: what the server publishes, kept up to date by its notifications, queried in memory.
var replica = app.GetRequiredService<IPersonCache>();
await replica.LoadAsync();
Console.WriteLine($"On the server: {string.Join(", ", replica.Entities.OrderBy(p => p.Id))}");
Console.WriteLine($"Added: {replica.Find(id)}");
