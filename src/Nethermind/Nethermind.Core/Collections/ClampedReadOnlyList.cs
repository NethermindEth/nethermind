// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Core.Test.Collections;

public class ClampedReadOnlyList<T> : IReadOnlyList<T>
{
    private IReadOnlyList<T> _baseImplementation;

    public ClampedReadOnlyList(IReadOnlyList<T> toClampList, int maxSize)
    {
        _baseImplementation = toClampList;
        Count = maxSize;
    }

    public IEnumerator<T> GetEnumerator()
    {
        return _baseImplementation.Take(Count).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        int counter = 0;
        while (counter < Count)
        {
            yield return _baseImplementation[counter];
            counter++;
        }
    }

    public int Count { get; }

    public T this[int index]
    {
        get
        {
            if (index >= Count)
            {
                throw new IndexOutOfRangeException();
            }
            return _baseImplementation[index];
        }
    }
}

public static class ReadOnlyListExtensions
{
    public static IReadOnlyList<T> Clamp<T>(this IReadOnlyList<T> toClampList, int maxSize)
    {
        if (toClampList.Count <= maxSize)
        {
            return toClampList;
        }

        return new ClampedReadOnlyList<T>(toClampList, maxSize);
    }
}
