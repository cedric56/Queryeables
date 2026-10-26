using MagicOnion;

namespace Queryeables.Onion;

public interface IRecordHubReceiver<TObject> where TObject : class
{
    void OnChanged(RecordMessage record);
}

public interface IRecordHub<TObject> :
    IStreamingHub<IRecordHub<TObject>, IRecordHubReceiver<TObject>>
    where TObject : class
{
    Task<Guid> AddReceiverAsync();
    Task RemoveAsync();
    void OnRecordChanged(RecordMessage e);
}
