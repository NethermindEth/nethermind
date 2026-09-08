// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;

namespace Nethermind.Core.Collections;

/// <summary>
/// Mark a list that is owned by the containing class/struct and should be disposed together with the class.
/// Conventionally:
/// - If this is returned from a method, the method caller should dispose it.
/// - If this is passed to a method, the receiving object for the method should dispose it.
/// You give it to me, I own it. I give it to you, you now own it. You own it, you clean it up la...
///
/// TODO: One day, check if https://github.com/dotnet/roslyn-analyzers/issues/1617 has progressed.
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IOwnedReadOnlyList<T> : IReadOnlyList<T>, IDisposable
{
    static IOwnedReadOnlyList<T> Empty => EmptyOwnedReadOnlyList.Instance;

    ReadOnlySpan<T> AsSpan();


    private sealed class EmptyOwnedReadOnlyList : IOwnedReadOnlyList<T>
    {
        public static EmptyOwnedReadOnlyList Instance { get; } = new();

        public int Count => 0;

        public T this[int index] => throw new ArgumentOutOfRangeException(nameof(index));

        public ReadOnlySpan<T> AsSpan() => [];

        public IEnumerator<T> GetEnumerator()
        {
            yield break;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void Dispose() { }
    }
}


public static class OwnedReadOnlyListExtensions
{
    public static IOwnedReadOnlyList<T> Slice<T>(this IOwnedReadOnlyList<T> list, int start, int count) =>
        new SlicedOwnedReadOnlyList<T>(list, start, count);

    public static IOwnedReadOnlyList<T> Slice<T>(this IOwnedReadOnlyList<T> list, int start) =>
        new SlicedOwnedReadOnlyList<T>(list, start, list.Count - start);

    public static void DisposeRecursive<T>(this IOwnedReadOnlyList<T> list) where T : IDisposable
    {
        if (list.Count != 0)
        {
            ReadOnlySpan<T> span = list.AsSpan();
            for (int i = 0; i < span.Length; i++)
            {
                span[i]?.Dispose();
            }
        }

        list.Dispose();
    }

    private sealed class SlicedOwnedReadOnlyList<T>(IOwnedReadOnlyList<T> list, int start, int count)
        : SlicedReadOnlyList<T>(list, start, count), IOwnedReadOnlyList<T>
    {
        public ReadOnlySpan<T> AsSpan() => ((IOwnedReadOnlyList<T>)_list).AsSpan().Slice(_start, Count);

        public void Dispose()
        {
            // The slice does not own the backing list; the caller disposes the original list.
        }
    }
}
