# Queryeables.Datasync

[Queryeables](https://github.com/cedric56/Queryeables) on the local database of a
[Datasync Community Toolkit](https://github.com/CommunityToolkit/Datasync) client: Datasync keeps the offline copy and
synchronizes it (push, pull, conflicts); Queryeables reads and writes it with its stores, caches and graphs.

```csharp
services.UseQueryeables(q => q
    .UseDatasync(sp => new AppDbContext(options))     // an OfflineDbContext: the writes are queued for PushAsync
    .AddGeneratedEntities());

await using var context = new AppDbContext(options);
await context.PushAsync();
await context.PullAsync().ThenRefreshCachesAsync(app);   // the caches follow what the pull wrote
```

`UpdateWhere` and `DeleteWhere` run in the database without the change tracker: Datasync does not push them. See
[offline with Datasync](https://github.com/cedric56/Queryeables/blob/master/docs/guides/offline-with-datasync.md).
