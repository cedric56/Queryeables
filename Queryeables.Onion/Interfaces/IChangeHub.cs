using MagicOnion;

namespace Queryeables.Onion;

public interface IChangeHubReceiver<TObject> where TObject : class
{
    void OnChanged(ChangeMessage record);
}

public interface IChangeHub<TObject> :
    IStreamingHub<IChangeHub<TObject>, IChangeHubReceiver<TObject>>
    where TObject : class
{
    Task<Guid> AddReceiverAsync();
    Task RemoveAsync();
    void OnChangeReceived(ChangeMessage e);
}
