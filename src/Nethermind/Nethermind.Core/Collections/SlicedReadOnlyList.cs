// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;

namespace Nethermind.Core.Collections;

/// <summary>
/// ChatGPT generated sliced read only list
/// </summary>
/// <typeparam name="T"></typeparam>
public class SlicedReadOnlyList<T> : IReadOnlyList<T>
{
    private readonly IReadOnlyList<T> _list;
    private readonly int _start;
    private readonly int _count;

    public SlicedReadOnlyList(IReadOnlyList<T> list, int start, int count)
    {
        if (list == null)
            throw new ArgumentNullException(nameof(list));
        if (start < 0 || start > list.Count)
            throw new ArgumentOutOfRangeException(nameof(start), "Start index is out of range.");
        if (count < 0 || start + count > list.Count)
            throw new ArgumentOutOfRangeException(nameof(count), "Count is out of range.");

        _list = list;
        _start = start;
        _count = count;
    }

    public T this[int index]
    {
        get
        {
            if (index < 0 || index >= _count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _list[_start + index];
        }
    }

    public int Count => _count;

    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < _count; i++)
        {
            yield return _list[_start + i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class ReadOnlyListExtensions
{
    public static IReadOnlyList<T> Slice<T>(this IReadOnlyList<T> list, int start, int count)
    {
        return new SlicedReadOnlyList<T>(list, start, count);
    }

    public static IReadOnlyList<T> Clamp<T>(this IReadOnlyList<T> list, int limit)
    {
        return new SlicedReadOnlyList<T>(list, 0, Math.Min(limit, list.Count));
    }

    // Extension method that slices from the start index to the end of the list
    public static IReadOnlyList<T> Slice<T>(this IReadOnlyList<T> list, int start)
    {
        if (list == null)
            throw new ArgumentNullException(nameof(list));
        if (start < 0 || start > list.Count)
            throw new ArgumentOutOfRangeException(nameof(start), "Start index is out of range.");

        return new SlicedReadOnlyList<T>(list, start, list.Count - start);
    }
}
