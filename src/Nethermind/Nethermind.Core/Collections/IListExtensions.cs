// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Core.Collections
{
    public static class ListExtensions
    {
        public static void ForEach<T>(this IReadOnlyList<T> list, Action<T> action)
        {
            for (int i = 0; i < list.Count; i++)
            {
                action(list[i]);
            }
        }

        public static T? GetItemRoundRobin<T>(this IList<T> array, long index) => array.Count == 0 ? default : array[(int)(index % array.Count)];

        /// <summary>
        /// Performs a binary search on the specified collection.
        /// </summary>
        /// <typeparam name="TItem">The type of the item.</typeparam>
        /// <typeparam name="TSearch">The type of the searched item.</typeparam>
        /// <param name="list">The list to be searched.</param>
        /// <param name="value">The value to search for.</param>
        /// <param name="comparer">The comparer that is used to compare the value
        /// with the list items.</param>
        /// <returns></returns>
        public static int BinarySearch<TItem, TSearch>(this IList<TItem> list, TSearch value, Func<TSearch, TItem, int> comparer)
        {
            if (list is null) throw new ArgumentNullException(nameof(list));
            if (comparer is null) throw new ArgumentNullException(nameof(comparer));

            int lower = 0;
            int upper = list.Count - 1;

            while (lower <= upper)
            {
                int middle = lower + (upper - lower) / 2;
                int comparisonResult = comparer(value, list[middle]);
                if (comparisonResult < 0)
                {
                    upper = middle - 1;
                }
                else if (comparisonResult > 0)
                {
                    lower = middle + 1;
                }
                else
                {
                    return middle;
                }
            }

            return ~lower;
        }

        /// <summary>
        /// Performs a binary search on the specified collection.
        /// </summary>
        /// <typeparam name="TItem">The type of the item.</typeparam>
        /// <param name="list">The list to be searched.</param>
        /// <param name="value">The value to search for.</param>
        /// <returns></returns>
        public static int BinarySearch<TItem>(this IList<TItem> list, TItem value) => BinarySearch(list, value, Comparer<TItem>.Default);

        /// <summary>
        /// Performs a binary search on the specified collection.
        /// </summary>
        /// <typeparam name="TItem">The type of the item.</typeparam>
        /// <param name="list">The list to be searched.</param>
        /// <param name="value">The value to search for.</param>
        /// <param name="comparer">The comparer that is used to compare the value
        /// with the list items.</param>
        /// <returns></returns>
        public static int BinarySearch<TItem>(this IList<TItem> list, TItem value, IComparer<TItem> comparer) => list.BinarySearch(value, comparer.Compare);

        public static bool TryGetSearchedItem<TComparable>(this IList<TComparable> list, in TComparable activation, out TComparable? item) where TComparable : IComparable<TComparable> =>
            list.TryGetSearchedItem(activation, (b, c) => b.CompareTo(c), out item);

        public static bool TryGetForBlock(this IList<long> list, in long blockNumber, out long item) =>
            list.TryGetSearchedItem(blockNumber, (b, c) => b.CompareTo(c), out item);

        public static bool TryGetSearchedItem<T, TComparable>(this IList<T> list, in TComparable searchedItem, Func<TComparable, T, int> comparer, out T? item)
        {
            int index = list.BinarySearch(searchedItem, comparer);
            return TryGetSearchedItem(list, index, out item);
        }

        private static bool TryGetSearchedItem<T>(this IList<T> list, int index, out T? item)
        {
            if (index >= 0)
            {
                item = list[index];
                return true;
            }
            else
            {
                int largerIndex = ~index;
                if (largerIndex != 0)
                {
                    item = list[largerIndex - 1];
                    return true;
                }
                else
                {
                    item = default;
                    return false;
                }
            }
        }
    }
}
