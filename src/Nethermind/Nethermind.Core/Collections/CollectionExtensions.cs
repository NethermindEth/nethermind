// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Nethermind.Core.Collections
{
    public static class CollectionExtensions
    {
        public static int LockPartitions { get; } = Environment.ProcessorCount * 16;

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

        public static void NoResizeClear<TKey, TValue>(this ConcurrentDictionary<TKey, TValue> dictionary)
                where TKey : notnull
        {
            using var handle = dictionary.AcquireLock();

            // We iterate over the keys and remove them one by one because calling Clear() on
            // the ConcurrentDictionary resets its capacity to 31 and then it has to constantly resize.
            foreach (TKey key in dictionary.Keys)
            {
                dictionary.TryRemove(key, out _);
            }
        }
    }
}

