// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Collections
{
    public static class CollectionExtensions
    {
        public static int LockPartitions { get; } = Environment.ProcessorCount * 16;

        public static void AddRange<T>(this ICollection<T> list, IEnumerable<T> items)
        {
            if (items is T[] array)
            {
                list.AddRange(array);
            }
            else if (items is IList<T> listItems)
            {
                list.AddRange(listItems);
            }
            else if (items is IReadOnlyList<T> readOnlyList)
            {
                list.AddRange(readOnlyList);
            }
            else
            {
                foreach (T item in items)
                {
                    list.Add(item);
                }
            }
        }

        [OverloadResolutionPriority(2)]
        public static void AddRange<T>(this ICollection<T> list, IList<T> items)
        {
            int count = items.Count;
            for (int index = 0; index < count; index++)
            {
                list.Add(items[index]);
            }
        }

        [OverloadResolutionPriority(1)]
        public static void AddRange<T>(this ICollection<T> list, IReadOnlyList<T> items)
        {
            int count = items.Count;
            for (int index = 0; index < count; index++)
            {
                list.Add(items[index]);
            }
        }

        [OverloadResolutionPriority(3)]
        public static void AddRange<T>(this ICollection<T> list, T[] items)
        {
            for (int index = 0; index < items.Length; index++)
            {
                list.Add(items[index]);
            }
        }

        public static bool NoResizeClear<TKey, TValue>(this ConcurrentDictionary<TKey, TValue>? dictionary)
                where TKey : notnull
        {
            if (dictionary?.IsEmpty ?? true)
            {
                return false;
            }

            using var handle = dictionary.AcquireLock();

            // Recheck under lock, so not to over clear which is expensive.
            // May have cleared while waiting for lock.
            if (dictionary.IsEmpty)
            {
                return false;
            }

            ClearCache<TKey, TValue>.Clear(dictionary);
            return true;
        }

        private static class ClearCache<TKey, TValue> where TKey : notnull
        {
            public static readonly Action<ConcurrentDictionary<TKey, TValue>> Clear = CreateNoResizeClearExpression();

            private static Action<ConcurrentDictionary<TKey, TValue>> CreateNoResizeClearExpression()
            {
                // Parameters
                var dictionaryParam = Expression.Parameter(typeof(ConcurrentDictionary<TKey, TValue>), "dictionary");

                // Access _tables field
                var tablesField = typeof(ConcurrentDictionary<TKey, TValue>).GetField("_tables", BindingFlags.NonPublic | BindingFlags.Instance);
                var tablesAccess = Expression.Field(dictionaryParam, tablesField!);

                // Access _buckets and _countPerLock fields
                var tablesType = tablesField!.FieldType;
                var bucketsField = tablesType.GetField("_buckets", BindingFlags.NonPublic | BindingFlags.Instance);
                var countPerLockField = tablesType.GetField("_countPerLock", BindingFlags.NonPublic | BindingFlags.Instance);

                var bucketsAccess = Expression.Field(tablesAccess, bucketsField!);
                var countPerLockAccess = Expression.Field(tablesAccess, countPerLockField!);

                // Clear arrays using Array.Clear
                var clearMethod = typeof(Array).GetMethod("Clear", new[] { typeof(Array), typeof(int), typeof(int) });

                var clearBuckets = Expression.Call(clearMethod!,
                    bucketsAccess,
                    Expression.Constant(0),
                    Expression.ArrayLength(bucketsAccess));

                var clearCountPerLock = Expression.Call(clearMethod!,
                    countPerLockAccess,
                    Expression.Constant(0),
                    Expression.ArrayLength(countPerLockAccess));

                // Block to execute both clears
                var block = Expression.Block(clearBuckets, clearCountPerLock);

                // Compile the expression into a lambda
                return Expression.Lambda<Action<ConcurrentDictionary<TKey, TValue>>>(block, name: "ConcurrentDictionary_FastClear", new ParameterExpression[] { dictionaryParam }).Compile();
            }
        }
    }
}

