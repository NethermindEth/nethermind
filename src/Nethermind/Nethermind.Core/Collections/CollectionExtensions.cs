// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core.Collections
{
    public static class CollectionExtensions
    {
        public static void AddRange<T>(this ICollection<T> list, IEnumerable<T> items)
        {
            foreach (T item in items)
            {
                list.Add(item);
            }
        }

        public static void AddRange<T>(this ICollection<T> list, params T[] items)
        {
            for (int index = 0; index < items.Length; index++)
            {
                list.Add(items[index]);
            }
        }
    }
}
