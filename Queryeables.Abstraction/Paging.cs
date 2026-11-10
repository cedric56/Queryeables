using System;

namespace Queryeables.Abstraction;

public static class Paging
{
    public static void Validate(int pageNumber, int pageSize)
    {
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber), "The first page is 1.");
        if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
        // (pageNumber - 1) * pageSize is the offset of the page: it must fit, or it turns negative.
        if ((long)(pageNumber - 1) * pageSize > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "The page is beyond any table: its offset does not fit.");
    }
}