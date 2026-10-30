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

    ///// <summary>
    ///// Synchronizes a target list collection with a set of selected items based on a predicate, 
    ///// adding missing items and removing unselected ones.
    ///// </summary>
    ///// <typeparam name="X">The type of source/selected items.</typeparam>
    ///// <typeparam name="Y">The type of items in the target list collection.</typeparam>
    ///// <param name="items">The target list collection to update in-place.</param>
    ///// <param name="selectedItems">The collection of selected source items representing the desired state.</param>
    ///// <param name="predicate">A function to test whether a source item matches a target list item.</param>
    ///// <param name="add">A factory function to instantiate a new target list item from a source item when added.</param>
    ////public static void Update<X, Y>(this IList<Y> items, IEnumerable<X> selectedItems, Func<X, Y, bool> predicate, Func<X, Y> add)
    //{
    //    foreach (var x in selectedItems)
    //        if (!items.Any(y => predicate(x, y)))
    //            items.Add(add(x));

    //    var deleted = items.Where(item => !selectedItems.Any(x => predicate(x, item))).ToList();
    //    foreach (var item in deleted)
    //        items.Remove(item);
    //}
}
