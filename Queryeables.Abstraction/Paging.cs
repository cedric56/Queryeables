using System;

namespace Queryeables.Abstraction;

public static class Paging
{
    public static void Validate(int pageNumber, int pageSize)
    {
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber), "La première page est 1.");
        if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
    }
}