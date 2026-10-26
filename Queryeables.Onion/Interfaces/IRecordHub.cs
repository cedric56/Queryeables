using MagicOnion;
using Queryeables.Abstraction;

namespace Queryeables.Onion;

public interface IRecordHubReceiver<TObject> where TObject : class
{
    void OnChanged(Record<TObject> record);
}

public interface IRecordHub<TObject> :
    IStreamingHub<IRecordHub<TObject>, IRecordHubReceiver<TObject>>
    where TObject : class
{
    Task<Guid> AddReceiverAsync();
    Task RemoveAsync();
    void OnRecordChanged(Record<TObject> e);
}
