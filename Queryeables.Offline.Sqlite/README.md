# Queryeables.Offline.Sqlite

Offline storage for [`Queryeables.Offline`](https://github.com/cedric56/Queryeables/blob/master/Queryeables.Offline/README.md):
`SqliteOfflineStorage` keeps the offline copy and its queue in one table of a SQLite file (WAL), on desktop and mobile.

```csharp
services.UseQueryeables(q => q
    .UseMagicOnion("https://server:5001")
    .AddGeneratedEntities()
    .WithOfflineMode(o => o
        .UseStorage(new SqliteOfflineStorage($"Data Source={Path.Combine(FileSystem.AppDataDirectory, "offline.db")}"))
        .AllGeneratedEntities()));
```

Wrap it in `EncryptedOfflineStorage` to encrypt what the device keeps.
