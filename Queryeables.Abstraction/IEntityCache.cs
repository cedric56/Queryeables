namespace Queryeables.Abstraction;

public interface IEntityCache<T>
{
    Task LoadEntities();
    event EventHandler<EventArgs>? EntitiesChanged;
    IEnumerable<T> Entities { get; }
    T? Find(long id);
    void Track(T entity);
}
