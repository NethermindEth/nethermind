//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;

namespace Nethermind.Consensus.AuRa
{
    public static class ListExtensions
    {
        public static T GetItemRoundRobin<T>(this IList<T> array, long index) => array.Count == 0 ? default : array[(int) (index % array.Count)];

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
            if (list == null) throw new ArgumentNullException(nameof(list));
            if (comparer == null) throw new ArgumentNullException(nameof(comparer));

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
        public static int BinarySearch<TItem>(this IList<TItem> list, TItem value)
        {
            return BinarySearch(list, value, Comparer<TItem>.Default);
        }

        /// <summary>
        /// Performs a binary search on the specified collection.
        /// </summary>
        /// <typeparam name="TItem">The type of the item.</typeparam>
        /// <param name="list">The list to be searched.</param>
        /// <param name="value">The value to search for.</param>
        /// <param name="comparer">The comparer that is used to compare the value
        /// with the list items.</param>
        /// <returns></returns>
        public static int BinarySearch<TItem>(this IList<TItem> list, TItem value,  IComparer<TItem> comparer)
        {
            return list.BinarySearch(value, comparer.Compare);
        }
        
        
        /// <summary>
        /// Tries to get a <see cref="IActivatedAtBlock"/> item for block <see cref="blockNumber"/>.
        /// </summary>
        /// <param name="list"></param>
        /// <param name="blockNumber"></param>
        /// <param name="item"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public static bool TryGetForBlock<T>(this IList<T> list, in long blockNumber, out T item) where T : IActivatedAtBlock => 
            list.TryGetForBlock(blockNumber, out item, activatedAtBlock => activatedAtBlock.ActivationBlock);

        public static bool TryGetForBlock<T>(this IList<T> list, in long blockNumber, out T item, Func<T, long> getActivatedAtBlock = null)
        {
            var index = list.BinarySearch(blockNumber, (b, c) => b.CompareTo(getActivatedAtBlock?.Invoke(c) ?? Convert.ToInt64(c)));
            if (index >= 0)
            {
                item = list[index];
                return true;
            }
            else
            {
                var largerIndex = ~index;
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