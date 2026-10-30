namespace Queryeables.Abstraction;

public static class CollectionExtensions
{
    /// <summary>
    /// Aligns <paramref name="target"/> with <paramref name="source"/>, in place: each source item is paired with one
    /// target item it matches, unpaired source items are created and added, and target items that match no source item
    /// are removed. Target items are never replaced, so references held elsewhere (bindings...) stay valid.
    /// </summary>
    /// <param name="match">True when the source item corresponds to the target item.</param>
    /// <param name="create">Creates the target item of a source item that has none.</param>
    /// <example>
    /// <code>
    /// // The contacts of a person follow the persons selected in a list.
    /// person.Contacts.Update(selectedPersons, (p, c) => c.PersonId == p.Id, p => new Contact { PersonId = p.Id });
    /// </code>
    /// </example>
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

        var sourceItems = source as IReadOnlyCollection<TSource> ?? source.ToList();

        // Target items not paired yet. A list of positions rather than a set of items: items with value semantics
        // (records, structs) that are equal stay distinct, and value types work.
        var unpaired = target.ToList();
        var added = new List<TTarget>();

        foreach (var sourceItem in sourceItems)
        {
            var index = unpaired.FindIndex(item => match(sourceItem, item));
            if (index >= 0)
                unpaired.RemoveAt(index);
            else
                added.Add(create(sourceItem));
        }

        // A duplicate that still matches a source item is kept, like before; the others go.
        foreach (var item in unpaired)
        {
            if (!sourceItems.Any(sourceItem => match(sourceItem, item)))
                target.Remove(item);
        }

        foreach (var item in added)
            target.Add(item);
    }
}
