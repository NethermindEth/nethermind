// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Collections;

namespace Nethermind.Core.Extensions
{
    public static class EnumerableExtensions
    {
        public static ISet<T> AsSet<T>(this IEnumerable<T> enumerable) =>
            enumerable as ISet<T> ?? enumerable.ToHashSet();

        public static ArrayPoolList<T> ToPooledList<T>(this IEnumerable<T> enumerable, int count) => new(count, enumerable);
        public static ArrayPoolList<T> ToPooledList<T>(this IReadOnlyCollection<T> collection) => new(collection.Count, collection);
        public static ArrayPoolList<T> ToPooledList<T>(this ReadOnlySpan<T> span) => new(span);
        public static ArrayPoolListRef<T> ToPooledListRef<T>(this IEnumerable<T> enumerable, int count) => new(count, enumerable);
        public static ArrayPoolListRef<T> ToPooledListRef<T>(this IReadOnlyCollection<T> collection) => new(collection.Count, collection);
        public static ArrayPoolListRef<T> ToPooledListRef<T>(this ReadOnlySpan<T> span) => new(span);

        public static (T Min, T Max) MinMax<T>(this IEnumerable<T> source)
            where T : IComparable<T>
        {
            T? min = default;
            T? max = default;
            bool hasValue = false;
            foreach (T item in source)
            {
                if (!hasValue)
                {
                    min = item;
                    max = item;
                    hasValue = true;
                }
                else
                {
                    if (item.CompareTo(min!) < 0) min = item;
                    if (item.CompareTo(max!) > 0) max = item;
                }
            }

            return hasValue
                ? (min!, max!)
                : throw new InvalidOperationException("Sequence contains no elements.");
        }

        public static IEnumerable<T> Shuffle<T>(this IEnumerable<T> enumerable, Random rng, int maxSize = 100)
        {
            using ArrayPoolList<T> buffer = new(maxSize, enumerable);
            for (int i = 0; i < buffer.Count; i++)
            {
                int j = rng.Next(i, buffer.Count);
                yield return buffer[j];

                buffer[j] = buffer[i];
            }
        }
    }
}
