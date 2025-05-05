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
