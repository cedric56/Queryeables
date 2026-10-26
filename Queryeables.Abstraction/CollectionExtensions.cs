namespace Queryeables.Abstraction;

public static class CollectionExtensions
{
    public static void Update<TSource, TTarget>(
        this ICollection<TTarget> target,
        IEnumerable<TSource> source,
        Func<TSource, TTarget, bool> match,
        Func<TSource, TTarget> create)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(create);

        var sourceItems = source.ToList();
        var matchedTargets = new HashSet<TTarget>();

        foreach (var sourceItem in sourceItems)
        {
            var existing = target.FirstOrDefault(
                item => !matchedTargets.Contains(item)
                     && match(sourceItem, item));

            if (existing is not null)
            {
                matchedTargets.Add(existing);
            }
            else
            {
                target.Add(create(sourceItem));
            }
        }

        // Remove items that do not match any source item
        foreach (var item in target.ToList())
        {
            if (!matchedTargets.Contains(item)
                && !sourceItems.Any(sourceItem => match(sourceItem, item)))
            {
                target.Remove(item);
            }
        }
    }
}
