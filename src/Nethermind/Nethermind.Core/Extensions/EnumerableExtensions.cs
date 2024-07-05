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

        public static IEnumerable<(T item, int index)> WithIndex<T>(this IEnumerable<T> self) =>
            self.Select((item, index) => (item, index));

        public static ArrayPoolList<T> ToPooledList<T>(this IEnumerable<T> enumerable, int count) => new(count, enumerable);
        public static ArrayPoolList<T> ToPooledList<T>(this IReadOnlyCollection<T> collection) => new(collection.Count, collection);

        public static IEnumerable<T> Shuffle<T>(this IEnumerable<T> enumerable)
        {
            return enumerable.Shuffle(new Random());
        }

        public static IEnumerable<T> Shuffle<T>(this IEnumerable<T> enumerable, Random rng)
        {
            return enumerable.ShuffleIterator(rng);
        }

        private static IEnumerable<T> ShuffleIterator<T>(this IEnumerable<T> enumerable, Random rng)
        {
            int size = enumerable.Count();
            List<T> buffer = enumerable.ToList();
            for (int i = 0; i < size; i++)
            {
                int j = rng.Next(i, size);
                yield return buffer[j];

                buffer[j] = buffer[i];
            }
        }
    }
}
