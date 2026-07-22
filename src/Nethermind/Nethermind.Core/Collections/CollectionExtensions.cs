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

        /// <summary>Default capacity above which <c>ClearAndTrim</c> shrinks a collection's backing storage.</summary>
        public const int DefaultTrimAboveCapacity = 8192;

        /// <summary>Default capacity <c>ClearAndTrim</c> shrinks a collection back to once <see cref="DefaultTrimAboveCapacity"/> is exceeded.</summary>
        public const int DefaultTrimToCapacity = 1024;

        public static void AddRange<T>(this ICollection<T> list, IEnumerable<T> items)
        {
            switch (items)
            {
                case T[] array:
                    list.AddRange(array);
                    break;
                case IList<T> listItems:
                    list.AddRange(listItems);
                    break;
                case IReadOnlyList<T> readOnlyList:
                    list.AddRange(readOnlyList);
                    break;
                default:
                    {
                        foreach (T item in items)
                        {
                            list.Add(item);
                        }

                        break;
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

        public static void AddOrUpdateRange<TKey, TValue>(this IDictionary<TKey, TValue> dict, IEnumerable<KeyValuePair<TKey, TValue>> items)
        {
            foreach (KeyValuePair<TKey, TValue> kv in items)
            {
                dict[kv.Key] = kv.Value;
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

        /// <summary>Clears the set, optionally shrinking its backing storage.</summary>
        /// <remarks>
        /// <see cref="HashSet{T}.Clear"/> retains the grown capacity, so a past burst permanently
        /// inflates the cost of every subsequent clear. Trimming once capacity exceeds
        /// <paramref name="trimAboveCapacity"/> lets those clears stop paying O(inflated capacity).
        /// To guarantee shrink-only behavior, <paramref name="trimToCapacity"/> must not exceed
        /// <paramref name="trimAboveCapacity"/>.
        /// </remarks>
        /// <param name="trimAboveCapacity">Only trim when the current capacity exceeds this value.</param>
        /// <param name="trimToCapacity">Capacity to shrink back to when trimming.</param>
        public static void ClearAndTrim<T>(this HashSet<T> set, int trimAboveCapacity = DefaultTrimAboveCapacity, int trimToCapacity = DefaultTrimToCapacity)
        {
            set.Clear();
            if (set.Capacity > trimAboveCapacity)
            {
                set.TrimExcess(trimToCapacity);
            }
        }

        public static bool NoResizeClear<TKey, TValue>(this ConcurrentDictionary<TKey, TValue>? dictionary)
                where TKey : notnull
        {
            if (dictionary is null)
            {
                return false;
            }

            // No unlocked precheck: a lock-free count scan can transiently read all-zero while a
            // concurrent writer holds entries, skipping a clear the caller asked for. Under the
            // stripes the counts are exact, and the scan is far cheaper than clearing a large
            // retained bucket array.
            using ConcurrentDictionaryLock<TKey, TValue>.Lock handle = dictionary.AcquireLock();
            return ClearIfHasEntries(dictionary);
        }

        /// <summary>
        /// Clears the dictionary in place like <see cref="NoResizeClear{TKey,TValue}"/>, retaining
        /// the grown bucket table, but without acquiring the stripe locks.
        /// </summary>
        /// <remarks>
        /// Requires complete quiescence: no concurrent writers or readers - a reader already
        /// mid-enumeration may still observe pre-clear entries. Stripe locks that have ever been
        /// contended stay inflated and route every acquisition through the Monitor slow path, so
        /// for large pooled maps the lock sweep dominates the cost of a locked clear.
        /// </remarks>
        /// <returns><c>true</c> when entries were cleared; <c>false</c> when already empty.</returns>
        public static bool NoLockClear<TKey, TValue>(this ConcurrentDictionary<TKey, TValue>? dictionary)
                where TKey : notnull =>
            dictionary is not null && ClearIfHasEntries(dictionary);

        private static bool ClearIfHasEntries<TKey, TValue>(ConcurrentDictionary<TKey, TValue> dictionary)
                where TKey : notnull
        {
            if (!HasEntries(dictionary))
            {
                return false;
            }

            ClearCache<TKey, TValue>.Clear(dictionary);
            return true;
        }

        /// <remarks>
        /// Reads the per-stripe counts without locks: <see cref="ConcurrentDictionary{TKey,TValue}.IsEmpty"/>
        /// acquires every stripe lock to CONFIRM an empty map, which is the common case for pooled
        /// clears. Exact only while the caller holds all stripes or the map is quiescent.
        /// </remarks>
        private static bool HasEntries<TKey, TValue>(ConcurrentDictionary<TKey, TValue> dictionary)
                where TKey : notnull
        {
            int[] countPerLock = ClearCache<TKey, TValue>.CountPerLock(dictionary);
            for (int i = 0; i < countPerLock.Length; i++)
            {
                if (countPerLock[i] != 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static class ClearCache<TKey, TValue> where TKey : notnull
        {
            public static readonly Action<ConcurrentDictionary<TKey, TValue>> Clear = CreateNoResizeClearExpression();
            public static readonly Func<ConcurrentDictionary<TKey, TValue>, int[]> CountPerLock = CreateCountPerLockGetter();

            private static Func<ConcurrentDictionary<TKey, TValue>, int[]> CreateCountPerLockGetter()
            {
                ParameterExpression dictionaryParam = Expression.Parameter(typeof(ConcurrentDictionary<TKey, TValue>), "dictionary");

                FieldInfo? tablesField = typeof(ConcurrentDictionary<TKey, TValue>).GetField("_tables", BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo? countPerLockField = tablesField!.FieldType.GetField("_countPerLock", BindingFlags.NonPublic | BindingFlags.Instance);

                MemberExpression countPerLockAccess = Expression.Field(Expression.Field(dictionaryParam, tablesField), countPerLockField!);

                return Expression.Lambda<Func<ConcurrentDictionary<TKey, TValue>, int[]>>(countPerLockAccess, dictionaryParam).Compile();
            }

            private static Action<ConcurrentDictionary<TKey, TValue>> CreateNoResizeClearExpression()
            {
                // Parameters
                ParameterExpression dictionaryParam = Expression.Parameter(typeof(ConcurrentDictionary<TKey, TValue>), "dictionary");

                // Access _tables field
                FieldInfo? tablesField = typeof(ConcurrentDictionary<TKey, TValue>).GetField("_tables", BindingFlags.NonPublic | BindingFlags.Instance);
                MemberExpression tablesAccess = Expression.Field(dictionaryParam, tablesField!);

                // Access _buckets and _countPerLock fields
                Type tablesType = tablesField!.FieldType;
                FieldInfo? bucketsField = tablesType.GetField("_buckets", BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo? countPerLockField = tablesType.GetField("_countPerLock", BindingFlags.NonPublic | BindingFlags.Instance);

                MemberExpression bucketsAccess = Expression.Field(tablesAccess, bucketsField!);
                MemberExpression countPerLockAccess = Expression.Field(tablesAccess, countPerLockField!);

                // Clear arrays using Array.Clear
                MethodInfo? clearMethod = typeof(Array).GetMethod("Clear", new[] { typeof(Array), typeof(int), typeof(int) });

                MethodCallExpression clearBuckets = Expression.Call(clearMethod!,
                    bucketsAccess,
                    Expression.Constant(0),
                    Expression.ArrayLength(bucketsAccess));

                MethodCallExpression clearCountPerLock = Expression.Call(clearMethod!,
                    countPerLockAccess,
                    Expression.Constant(0),
                    Expression.ArrayLength(countPerLockAccess));

                // Block to execute both clears
                BlockExpression block = Expression.Block(clearBuckets, clearCountPerLock);

                // Compile the expression into a lambda
                return Expression.Lambda<Action<ConcurrentDictionary<TKey, TValue>>>(block, name: "ConcurrentDictionary_FastClear", new ParameterExpression[] { dictionaryParam }).Compile();
            }
        }
    }
}

