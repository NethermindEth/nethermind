using System.Collections.Generic;

namespace Nethermind.Core.Collections;

public static class IReadOnlyListExtensions
{
    public static IReadOnlyList<T> CappedTo<T>(this IReadOnlyList<T> readOnlyList, int length)
    {
        if (length > readOnlyList.Count)
        {
            return readOnlyList;
        }

        return new CappedReadOnlyList<T>(readOnlyList, length);
    }
}
