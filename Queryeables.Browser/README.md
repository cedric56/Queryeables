# Queryeables.Browser

Offline storage for [Queryeables](https://github.com/cedric56/Queryeables) in Blazor WebAssembly: `IndexedDbOfflineStorage`
keeps the offline copy and its queue in IndexedDB, without the 5 MB limit of `localStorage`.

```csharp
builder.Services.UseQueryeables(q => q
    .UseMagicOnion("https://server:5001")
    .AddGeneratedEntities()
    .WithOfflineMode(o => o
        .UseStorage(new IndexedDbOfflineStorage())
        .Entity<Person, long>(p => p.Id)));

var host = builder.Build();
await host.Services.GetRequiredService<OfflineMode>().InitializeAsync();
await host.RunAsync();
```

The JavaScript module ships with the package and is loaded on first use from
`_content/Queryeables.Browser/queryeables-indexeddb.js`: nothing to add to `index.html`.
