namespace Queryeables.Abstraction;

/// <summary>
/// What a client keeps for the signed-in user: the caches, the offline copy. When the user changes (sign-out, another
/// account), it must be forgotten before the next user sees anything: <see cref="UserScopedData.ChangeUserAsync"/> does it
/// for all of them.
/// </summary>
public interface IUserScopedData
{
    /// <summary>
    /// True when forgetting would lose work of the current user (offline writes not sent yet): the change of user is then
    /// refused until they are synchronized or discarded.
    /// </summary>
    bool HasUnsavedChanges => false;

    /// <summary>Forgets everything kept for the current user. Never reaches the server: it works without network.</summary>
    Task ClearUserDataAsync(CancellationToken ct = default);

    /// <summary>Loads again, for the new user, what was loaded before (a cache that was loaded); nothing by default.</summary>
    Task ReloadUserDataAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public static class UserScopedData
{
    /// <summary>
    /// Changes the user: checks that nothing unsaved would be lost, forgets the data of every <paramref name="data"/>, then,
    /// when <paramref name="reload"/> is true, loads again what was loaded, now for the new user. Everything is forgotten
    /// before anything is loaded: a failed reload leaves empty data, never the previous user's.
    /// </summary>
    /// <exception cref="InvalidOperationException">Offline writes are waiting: synchronize or discard them first. Nothing was forgotten.</exception>
    /// <exception cref="AggregateException">Some reloads failed (no network...); everything was forgotten anyway.</exception>
    public static async Task ChangeUserAsync(IEnumerable<IUserScopedData> data, bool reload = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var all = data.Distinct().ToList();
        if (all.Any(d => d.HasUnsavedChanges))
            throw new InvalidOperationException(
                "Writes of the current user are waiting to be sent: synchronize them (OfflineMode.GoOnlineAsync) or discard them " +
                "(OfflineMode.DiscardPendingChangesAsync) before changing user.");

        foreach (var item in all)
            await item.ClearUserDataAsync(ct);
        if (!reload)
            return;

        var failures = new List<Exception>();
        foreach (var item in all)
        {
            try
            {
                await item.ReloadUserDataAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex);
            }
        }
        if (failures.Count > 0)
            throw new AggregateException("The data of the new user could not all be loaded; the previous user's data was forgotten.", failures);
    }
}
