// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;

namespace Nethermind.CoreOfCore
{
    /// <summary>
    /// A lightweight, eager-evaluation implementation that does not depend on System.Linq.
    /// Refactored to remove 'yield return' state machines for compatibility with custom RISC-V compilers.
    /// </summary>
    public static class Enumerable
    {
        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        private static void ThrowIfNull(object arg, string name)
        {
            if (arg == null)
                throw new ArgumentNullException(name);
        }

        // ---------------------------------------------------------------------
        // WHERE
        // ---------------------------------------------------------------------

        public static IEnumerable<TSource> Where<TSource>(
            this IEnumerable<TSource> source,
            Func<TSource, bool> predicate)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(predicate, nameof(predicate));

            var results = new List<TSource>();
            foreach (var item in source)
            {
                if (predicate(item))
                {
                    results.Add(item);
                }
            }
            return results;
        }

        // ---------------------------------------------------------------------
        // SELECT
        // ---------------------------------------------------------------------

        public static IEnumerable<TResult> Select<TSource, TResult>(
            this IEnumerable<TSource> source,
            Func<TSource, TResult> selector)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(selector, nameof(selector));

            var results = new List<TResult>();
            foreach (var item in source)
            {
                results.Add(selector(item));
            }
            return results;
        }

        public static IEnumerable<TResult> Select<TSource, TResult>(
            this IEnumerable<TSource> source,
            Func<TSource, int, TResult> selector)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(selector, nameof(selector));

            var results = new List<TResult>();
            int index = 0;
            foreach (var item in source)
            {
                results.Add(selector(item, index));
                index++;
            }
            return results;
        }

        // ---------------------------------------------------------------------
        // SELECTMANY
        // ---------------------------------------------------------------------

        public static IEnumerable<TResult> SelectMany<TSource, TResult>(
            this IEnumerable<TSource> source,
            Func<TSource, IEnumerable<TResult>> selector)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(selector, nameof(selector));

            var results = new List<TResult>();
            foreach (var item in source)
            {
                var inner = selector(item);
                if (inner != null)
                {
                    foreach (var result in inner)
                    {
                        results.Add(result);
                    }
                }
            }
            return results;
        }

        // ---------------------------------------------------------------------
        // ANY / ALL / COUNT (Already eager, mostly unchanged)
        // ---------------------------------------------------------------------

        public static bool Any<TSource>(this IEnumerable<TSource> source)
        {
            ThrowIfNull(source, nameof(source));
            foreach (var _ in source) return true;
            return false;
        }

        public static bool Any<TSource>(
            this IEnumerable<TSource> source,
            Func<TSource, bool> predicate)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(predicate, nameof(predicate));
            foreach (var item in source)
            {
                if (predicate(item)) return true;
            }
            return false;
        }

        public static bool All<TSource>(
            this IEnumerable<TSource> source,
            Func<TSource, bool> predicate)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(predicate, nameof(predicate));
            foreach (var item in source)
            {
                if (!predicate(item)) return false;
            }
            return true;
        }

        public static int Count<TSource>(this IEnumerable<TSource> source)
        {
            ThrowIfNull(source, nameof(source));
            // Optimization for standard collections
            if (source is ICollection<TSource> c) return c.Count;
            if (source is ICollection cLegacy) return cLegacy.Count;

            int count = 0;
            foreach (var _ in source) checked { count++; }
            return count;
        }

        public static int Count<TSource>(
            this IEnumerable<TSource> source,
            Func<TSource, bool> predicate)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(predicate, nameof(predicate));
            int count = 0;
            foreach (var item in source)
            {
                if (predicate(item)) checked { count++; }
            }
            return count;
        }

        // ---------------------------------------------------------------------
        // FIRST / SINGLE / LAST
        // ---------------------------------------------------------------------

        public static TSource First<TSource>(this IEnumerable<TSource> source)
        {
            ThrowIfNull(source, nameof(source));
            foreach (var item in source) return item;
            throw new InvalidOperationException("Sequence contains no elements.");
        }

        public static TSource First<TSource>(
            this IEnumerable<TSource> source,
            Func<TSource, bool> predicate)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(predicate, nameof(predicate));
            foreach (var item in source)
            {
                if (predicate(item)) return item;
            }
            throw new InvalidOperationException("Sequence contains no matching element.");
        }

        public static TSource FirstOrDefault<TSource>(this IEnumerable<TSource> source)
        {
            ThrowIfNull(source, nameof(source));
            foreach (var item in source) return item;
            return default!;
        }

        public static TSource FirstOrDefault<TSource>(
            this IEnumerable<TSource> source,
            Func<TSource, bool> predicate)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(predicate, nameof(predicate));
            foreach (var item in source)
            {
                if (predicate(item)) return item;
            }
            return default!;
        }

        public static TSource FirstOrDefault<TSource>(
           this IEnumerable<TSource> source,
           Func<TSource, bool> predicate,
           TSource defaultValue)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(predicate, nameof(predicate));
            foreach (var item in source)
            {
                if (predicate(item)) return item;
            }
            return defaultValue;
        }

        public static TSource Single<TSource>(this IEnumerable<TSource> source)
        {
            ThrowIfNull(source, nameof(source));
            bool found = false;
            TSource result = default!;
            foreach (var item in source)
            {
                if (found) throw new InvalidOperationException("Sequence contains more than one element.");
                found = true;
                result = item;
            }
            if (!found) throw new InvalidOperationException("Sequence contains no elements.");
            return result;
        }

        public static TSource Single<TSource>(
            this IEnumerable<TSource> source,
            Func<TSource, bool> predicate)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(predicate, nameof(predicate));
            bool found = false;
            TSource result = default!;
            foreach (var item in source)
            {
                if (!predicate(item)) continue;
                if (found) throw new InvalidOperationException("Sequence contains more than one matching element.");
                found = true;
                result = item;
            }
            if (!found) throw new InvalidOperationException("Sequence contains no matching element.");
            return result;
        }

        public static TSource SingleOrDefault<TSource>(this IEnumerable<TSource> source)
        {
            ThrowIfNull(source, nameof(source));
            bool found = false;
            TSource result = default!;
            foreach (var item in source)
            {
                if (found) throw new InvalidOperationException("Sequence contains more than one element.");
                found = true;
                result = item;
            }
            return result;
        }

        public static TSource SingleOrDefault<TSource>(
            this IEnumerable<TSource> source,
            Func<TSource, bool> predicate)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(predicate, nameof(predicate));
            bool found = false;
            TSource result = default!;
            foreach (var item in source)
            {
                if (!predicate(item)) continue;
                if (found) throw new InvalidOperationException("Sequence contains more than one matching element.");
                found = true;
                result = item;
            }
            return result;
        }

        public static TSource Last<TSource>(this IEnumerable<TSource> source)
        {
            ThrowIfNull(source, nameof(source));
            TSource result = default!;
            bool found = false;
            foreach (var item in source)
            {
                found = true;
                result = item;
            }
            if (!found) throw new InvalidOperationException("Sequence contains no elements.");
            return result;
        }

        public static TSource Last<TSource>(
            this IEnumerable<TSource> source,
            Func<TSource, bool> predicate)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(predicate, nameof(predicate));
            TSource result = default!;
            bool found = false;
            foreach (var item in source)
            {
                if (predicate(item))
                {
                    found = true;
                    result = item;
                }
            }
            if (!found) throw new InvalidOperationException("Sequence contains no matching element.");
            return result;
        }

        public static TSource? LastOrDefault<TSource>(this IEnumerable<TSource> source)
        {
            ThrowIfNull(source, nameof(source));
            TSource? last = default;
            bool found = false;
            foreach (var item in source)
            {
                last = item;
                found = true;
            }
            return found ? last : default;
        }

        public static TSource? LastOrDefault<TSource>(
            this IEnumerable<TSource> source,
            Func<TSource, bool> predicate)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(predicate, nameof(predicate));
            TSource? last = default;
            bool found = false;
            foreach (var item in source)
            {
                if (predicate(item))
                {
                    last = item;
                    found = true;
                }
            }
            return found ? last : default;
        }

        // ---------------------------------------------------------------------
        // CONTAINS / SEQUENCE EQUAL
        // ---------------------------------------------------------------------

        public static bool Contains<TSource>(
            this IEnumerable<TSource> source,
            TSource value)
        {
            return Contains(source, value, EqualityComparer<TSource>.Default);
        }

        public static bool Contains<TSource>(
            this IEnumerable<TSource> source,
            TSource value,
            IEqualityComparer<TSource> comparer)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(comparer, nameof(comparer));
            foreach (var item in source)
            {
                if (comparer.Equals(item, value)) return true;
            }
            return false;
        }

        public static bool SequenceEqual<TSource>(
            this IEnumerable<TSource> first,
            IEnumerable<TSource> second)
        {
            return SequenceEqual(first, second, EqualityComparer<TSource>.Default);
        }

        public static bool SequenceEqual<TSource>(
            this IEnumerable<TSource> first,
            IEnumerable<TSource> second,
            IEqualityComparer<TSource> comparer)
        {
            ThrowIfNull(first, nameof(first));
            ThrowIfNull(second, nameof(second));
            ThrowIfNull(comparer, nameof(comparer));

            using (var e1 = first.GetEnumerator())
            using (var e2 = second.GetEnumerator())
            {
                while (true)
                {
                    bool moved1 = e1.MoveNext();
                    bool moved2 = e2.MoveNext();
                    if (moved1 != moved2) return false; // lengths differ
                    if (!moved1) return true; // both finished
                    if (!comparer.Equals(e1.Current, e2.Current)) return false;
                }
            }
        }

        // ---------------------------------------------------------------------
        // SET OPERATIONS (Distinct, Union, Concat)
        // ---------------------------------------------------------------------

        public static IEnumerable<TSource> Distinct<TSource>(this IEnumerable<TSource> source)
        {
            return Distinct(source, EqualityComparer<TSource>.Default);
        }

        public static IEnumerable<TSource> Distinct<TSource>(
            this IEnumerable<TSource> source,
            IEqualityComparer<TSource> comparer)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(comparer, nameof(comparer));

            var results = new List<TSource>();
            var set = new HashSet<TSource>(comparer);
            foreach (var item in source)
            {
                if (set.Add(item))
                    results.Add(item);
            }
            return results;
        }

        public static IEnumerable<TSource> DistinctBy<TSource, TKey>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector)
        {
            return DistinctBy(source, keySelector, null);
        }

        public static IEnumerable<TSource> DistinctBy<TSource, TKey>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector,
            IEqualityComparer<TKey>? comparer)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(keySelector, nameof(keySelector));

            var results = new List<TSource>();
            var seen = new HashSet<TKey>(comparer);
            foreach (var item in source)
            {
                if (seen.Add(keySelector(item)))
                    results.Add(item);
            }
            return results;
        }

        public static IEnumerable<TSource> Union<TSource>(
            this IEnumerable<TSource> first,
            IEnumerable<TSource> second)
        {
            return Union(first, second, EqualityComparer<TSource>.Default);
        }

        public static IEnumerable<TSource> Union<TSource>(
            this IEnumerable<TSource> first,
            IEnumerable<TSource> second,
            IEqualityComparer<TSource>? comparer)
        {
            ThrowIfNull(first, nameof(first));
            ThrowIfNull(second, nameof(second));

            var results = new List<TSource>();
            var set = new HashSet<TSource>(comparer);

            foreach (var item in first)
            {
                if (set.Add(item)) results.Add(item);
            }
            foreach (var item in second)
            {
                if (set.Add(item)) results.Add(item);
            }
            return results;
        }

        public static IEnumerable<TSource> Concat<TSource>(
            this IEnumerable<TSource> first,
            IEnumerable<TSource> second)
        {
            ThrowIfNull(first, nameof(first));
            ThrowIfNull(second, nameof(second));

            var results = new List<TSource>();
            foreach (var item in first) results.Add(item);
            foreach (var item in second) results.Add(item);
            return results;
        }

        public static HashSet<TSource> ToHashSet<TSource>(this IEnumerable<TSource> source)
        {
            return ToHashSet(source, null);
        }

        public static HashSet<TSource> ToHashSet<TSource>(
            this IEnumerable<TSource> source,
            IEqualityComparer<TSource>? comparer)
        {
            ThrowIfNull(source, nameof(source));
            // ToHashSet constructor already iterates immediately
            return new HashSet<TSource>(source, comparer);
        }

        // ---------------------------------------------------------------------
        // PARTITIONING (Take, Skip, Chunk)
        // ---------------------------------------------------------------------

        public static IEnumerable<TSource> Take<TSource>(
            this IEnumerable<TSource> source,
            int count)
        {
            ThrowIfNull(source, nameof(source));
            var results = new List<TSource>();
            if (count <= 0) return results;

            int taken = 0;
            foreach (var item in source)
            {
                results.Add(item);
                taken++;
                if (taken >= count) break;
            }
            return results;
        }

        public static IEnumerable<TSource> TakeWhile<TSource>(
            this IEnumerable<TSource> source,
            Func<TSource, bool> predicate)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(predicate, nameof(predicate));

            var results = new List<TSource>();
            foreach (var item in source)
            {
                if (!predicate(item)) break;
                results.Add(item);
            }
            return results;
        }

        public static IEnumerable<TSource> Skip<TSource>(
            this IEnumerable<TSource> source,
            int count)
        {
            ThrowIfNull(source, nameof(source));
            var results = new List<TSource>();
            int skipped = 0;

            foreach (var item in source)
            {
                if (count > 0 && skipped < count)
                {
                    skipped++;
                    continue;
                }
                results.Add(item);
            }
            return results;
        }

        public static IEnumerable<TSource> SkipWhile<TSource>(
            this IEnumerable<TSource> source,
            Func<TSource, bool> predicate)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(predicate, nameof(predicate));

            var results = new List<TSource>();
            bool yielding = false;

            foreach (var item in source)
            {
                if (!yielding && !predicate(item))
                    yielding = true;

                if (yielding)
                    results.Add(item);
            }
            return results;
        }

        public static IEnumerable<TSource> SkipWhile<TSource>(
            this IEnumerable<TSource> source,
            Func<TSource, int, bool> predicate)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(predicate, nameof(predicate));

            var results = new List<TSource>();
            bool yielding = false;
            int index = 0;

            foreach (var item in source)
            {
                if (!yielding)
                {
                    if (!predicate(item, index))
                        yielding = true;
                }

                if (yielding)
                    results.Add(item);

                index++;
            }
            return results;
        }

        public static IEnumerable<TSource> SkipLast<TSource>(
            this IEnumerable<TSource> source,
            int count)
        {
            ThrowIfNull(source, nameof(source));
            // Eager evaluation: load all, return subset
            var all = new List<TSource>(source);
            if (count <= 0) return all;
            if (all.Count <= count) return new List<TSource>();

            // Remove last N elements
            all.RemoveRange(all.Count - count, count);
            return all;
        }

        public static IEnumerable<TSource[]> Chunk<TSource>(
            this IEnumerable<TSource> source,
            int size)
        {
            ThrowIfNull(source, nameof(source));
            if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));

            var results = new List<TSource[]>();
            var currentChunk = new List<TSource>(size);

            foreach (var item in source)
            {
                currentChunk.Add(item);
                if (currentChunk.Count == size)
                {
                    results.Add(currentChunk.ToArray());
                    currentChunk = new List<TSource>(size);
                }
            }

            if (currentChunk.Count > 0)
            {
                results.Add(currentChunk.ToArray());
            }

            return results;
        }

        // ---------------------------------------------------------------------
        // CONVERSION
        // ---------------------------------------------------------------------

        public static TSource[] ToArray<TSource>(this IEnumerable<TSource> source)
        {
            ThrowIfNull(source, nameof(source));
            return new List<TSource>(source).ToArray();
        }

        public static List<TSource> ToList<TSource>(this IEnumerable<TSource> source)
        {
            ThrowIfNull(source, nameof(source));
            return new List<TSource>(source);
        }

        public static Dictionary<TKey, TElement> ToDictionary<TSource, TKey, TElement>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector,
            Func<TSource, TElement> elementSelector)
            where TKey : notnull
        {
            return ToDictionary(source, keySelector, elementSelector, null);
        }

        public static Dictionary<TKey, TElement> ToDictionary<TSource, TKey, TElement>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector,
            Func<TSource, TElement> elementSelector,
            IEqualityComparer<TKey>? comparer)
            where TKey : notnull
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(keySelector, nameof(keySelector));
            ThrowIfNull(elementSelector, nameof(elementSelector));

            var dict = new Dictionary<TKey, TElement>(comparer);
            foreach (var item in source)
            {
                var key = keySelector(item);
                if (dict.ContainsKey(key))
                    throw new ArgumentException("An element with the same key already exists.", nameof(key));
                dict[key] = elementSelector(item);
            }
            return dict;
        }

        public static Dictionary<TKey, TSource> ToDictionary<TSource, TKey>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector)
            where TKey : notnull
        {
            return ToDictionary(source, keySelector, x => x, null);
        }

        public static IEnumerable<TResult> Cast<TResult>(this IEnumerable source)
        {
            ThrowIfNull(source, nameof(source));
            var results = new List<TResult>();
            foreach (var item in source)
            {
                results.Add((TResult)item!);
            }
            return results;
        }

        public static IEnumerable<TResult> OfType<TResult>(this IEnumerable source)
        {
            ThrowIfNull(source, nameof(source));
            var results = new List<TResult>();
            foreach (var item in source)
            {
                if (item is TResult result)
                    results.Add(result);
            }
            return results;
        }

        // ---------------------------------------------------------------------
        // AGGREGATION / MATH
        // ---------------------------------------------------------------------

        public static TAccumulate Aggregate<TSource, TAccumulate>(
            this IEnumerable<TSource> source,
            TAccumulate seed,
            Func<TAccumulate, TSource, TAccumulate> func)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(func, nameof(func));
            var acc = seed;
            foreach (var item in source)
            {
                acc = func(acc, item);
            }
            return acc;
        }

        public static double Min(this IEnumerable<double> source)
        {
            ThrowIfNull(source, nameof(source));
            using var e = source.GetEnumerator();
            if (!e.MoveNext()) throw new InvalidOperationException("Sequence contains no elements.");
            double min = e.Current;
            while (e.MoveNext())
            {
                if (e.Current < min) min = e.Current;
            }
            return min;
        }

        public static int Sum(this IEnumerable<int> source)
        {
            ThrowIfNull(source, nameof(source));
            int sum = 0;
            checked { foreach (var item in source) sum += item; }
            return sum;
        }

        public static long Sum(this IEnumerable<long> source)
        {
            ThrowIfNull(source, nameof(source));
            long sum = 0;
            checked { foreach (var item in source) sum += item; }
            return sum;
        }

        public static double Sum(this IEnumerable<double> source)
        {
            ThrowIfNull(source, nameof(source));
            double sum = 0;
            foreach (var item in source) sum += item;
            return sum;
        }

        public static float Sum(this IEnumerable<float> source)
        {
            ThrowIfNull(source, nameof(source));
            float sum = 0;
            foreach (var item in source) sum += item;
            return sum;
        }

        public static decimal Sum(this IEnumerable<decimal> source)
        {
            ThrowIfNull(source, nameof(source));
            decimal sum = 0;
            foreach (var item in source) sum += item;
            return sum;
        }

        public static long Sum<TSource>(
            this IEnumerable<TSource> source,
            Func<TSource, long> selector)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(selector, nameof(selector));
            long sum = 0;
            checked { foreach (var item in source) sum += selector(item); }
            return sum;
        }

        // ---------------------------------------------------------------------
        // ORDERBY / THENBY (Eager implementation)
        // ---------------------------------------------------------------------

        public static IOrderedEnumerable<TSource> OrderBy<TSource, TKey>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector)
        {
            return OrderBy(source, keySelector, Comparer<TKey>.Default);
        }

        public static IOrderedEnumerable<TSource> OrderBy<TSource, TKey>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector,
            IComparer<TKey> comparer)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(keySelector, nameof(keySelector));
            ThrowIfNull(comparer, nameof(comparer));

            return new OrderedEnumerable<TSource>(source).CreateOrderedEnumerable(keySelector, comparer, false);
        }

        public static IOrderedEnumerable<TSource> OrderByDescending<TSource, TKey>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector)
        {
            return OrderByDescending(source, keySelector, Comparer<TKey>.Default);
        }

        public static IOrderedEnumerable<TSource> OrderByDescending<TSource, TKey>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector,
            IComparer<TKey> comparer)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(keySelector, nameof(keySelector));
            ThrowIfNull(comparer, nameof(comparer));

            return new OrderedEnumerable<TSource>(source).CreateOrderedEnumerable(keySelector, comparer, true);
        }

        public static IOrderedEnumerable<TSource> ThenByDescending<TSource, TKey>(
            this IOrderedEnumerable<TSource> source,
            Func<TSource, TKey> keySelector)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            return source.CreateOrderedEnumerable(keySelector, Comparer<TKey>.Default, true);
        }

        // ---------------------------------------------------------------------
        // GROUPBY
        // ---------------------------------------------------------------------

        public static IEnumerable<IGrouping<TKey, TSource>> GroupBy<TSource, TKey>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector)
            where TKey : notnull
        {
            return GroupBy(source, keySelector, x => x, null);
        }

        public static IEnumerable<IGrouping<TKey, TSource>> GroupBy<TSource, TKey>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector,
            IEqualityComparer<TKey>? comparer)
            where TKey : notnull
        {
            return GroupBy(source, keySelector, x => x, comparer);
        }

        public static IEnumerable<IGrouping<TKey, TElement>> GroupBy<TSource, TKey, TElement>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector,
            Func<TSource, TElement> elementSelector)
            where TKey : notnull
        {
            return GroupBy(source, keySelector, elementSelector, null);
        }

        public static IEnumerable<IGrouping<TKey, TElement>> GroupBy<TSource, TKey, TElement>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector,
            Func<TSource, TElement> elementSelector,
            IEqualityComparer<TKey>? comparer)
            where TKey : notnull
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(keySelector, nameof(keySelector));
            ThrowIfNull(elementSelector, nameof(elementSelector));

            var groups = new Dictionary<TKey, List<TElement>>(comparer);
            foreach (var item in source)
            {
                var key = keySelector(item);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<TElement>();
                    groups[key] = list;
                }
                list.Add(elementSelector(item));
            }

            // Eagerly convert to list of Grouping objects
            var resultList = new List<IGrouping<TKey, TElement>>();
            foreach (var kvp in groups)
            {
                resultList.Add(new Grouping<TKey, TElement>(kvp.Key, kvp.Value));
            }
            return resultList;
        }

        // ---------------------------------------------------------------------
        // GENERATION
        // ---------------------------------------------------------------------

        public static IEnumerable<int> Range(int start, int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), "Count cannot be negative.");
            long max = (long)start + (long)count - 1;
            if (max > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(count), "Range exceeds Int32 max value.");

            var results = new List<int>(count);
            for (int i = 0; i < count; i++)
                results.Add(start + i);
            return results;
        }

        public static IEnumerable<TSource> Repeat<TSource>(TSource element, int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            var results = new List<TSource>(count);
            for (int i = 0; i < count; i++)
                results.Add(element);
            return results;
        }

        public static IEnumerable<T> Empty<T>()
        {
            return new T[0];
        }

        // ---------------------------------------------------------------------
        // OTHER
        // ---------------------------------------------------------------------

        public static IEnumerable<TSource> AsEnumerable<TSource>(this IEnumerable<TSource> source)
        {
            ThrowIfNull(source, nameof(source));
            return source;
        }

        public static IEnumerable<TSource> Reverse<TSource>(this IEnumerable<TSource> source)
        {
            ThrowIfNull(source, nameof(source));
            var list = new List<TSource>(source);
            list.Reverse();
            return list;
        }

        public static IEnumerable<TResult> Zip<TFirst, TSecond, TResult>(
            this IEnumerable<TFirst> first,
            IEnumerable<TSecond> second,
            Func<TFirst, TSecond, TResult> resultSelector)
        {
            ThrowIfNull(first, nameof(first));
            ThrowIfNull(second, nameof(second));
            ThrowIfNull(resultSelector, nameof(resultSelector));

            var results = new List<TResult>();
            using (var e1 = first.GetEnumerator())
            using (var e2 = second.GetEnumerator())
            {
                while (e1.MoveNext() && e2.MoveNext())
                {
                    results.Add(resultSelector(e1.Current, e2.Current));
                }
            }
            return results;
        }

        public static IEnumerable<TResult> Zip<TSource, TResult>(
            this IEnumerable<TSource> source,
            Func<TSource, int, TResult> resultSelector)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(resultSelector, nameof(resultSelector));

            var results = new List<TResult>();
            int index = 0;
            foreach (var item in source)
                results.Add(resultSelector(item, index++));
            return results;
        }

        public static IEnumerable<(TFirst First, TSecond Second)> Zip<TFirst, TSecond>(
            this IEnumerable<TFirst> first,
            IEnumerable<TSecond> second)
        {
            ThrowIfNull(first, nameof(first));
            ThrowIfNull(second, nameof(second));

            var results = new List<(TFirst, TSecond)>();
            using var e1 = first.GetEnumerator();
            using var e2 = second.GetEnumerator();
            while (e1.MoveNext() && e2.MoveNext())
                results.Add((e1.Current, e2.Current));

            return results;
        }
    }

    // ---------------------------------------------------------------------
    // SUPPORTING TYPES
    // ---------------------------------------------------------------------

    public interface IGrouping<TKey, TElement> : IEnumerable<TElement>
    {
        TKey Key { get; }
    }

    internal sealed class Grouping<TKey, TElement> : IGrouping<TKey, TElement>
    {
        private readonly List<TElement> _elements;
        public TKey Key { get; }

        public Grouping(TKey key, List<TElement> elements)
        {
            Key = key;
            _elements = elements;
        }

        public IEnumerator<TElement> GetEnumerator() => _elements.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public interface IOrderedEnumerable<T> : IEnumerable<T>
    {
        IOrderedEnumerable<T> CreateOrderedEnumerable<TKey>(
            Func<T, TKey> keySelector,
            IComparer<TKey>? comparer,
            bool descending);
    }

    // A concrete implementation that performs the sorting immediately when enumerated
    // This avoids complex LINQ state machines.
    internal class OrderedEnumerable<TElement> : IOrderedEnumerable<TElement>
    {
        private readonly IEnumerable<TElement> _source;
        // List of comparison functions to support ThenBy
        private readonly List<Func<TElement, TElement, int>> _comparers;

        public OrderedEnumerable(IEnumerable<TElement> source)
        {
            _source = source;
            _comparers = new List<Func<TElement, TElement, int>>();
        }

        private OrderedEnumerable(IEnumerable<TElement> source, List<Func<TElement, TElement, int>> comparers)
        {
            _source = source;
            _comparers = comparers;
        }

        public IOrderedEnumerable<TElement> CreateOrderedEnumerable<TKey>(
            Func<TElement, TKey> keySelector,
            IComparer<TKey>? comparer,
            bool descending)
        {
            comparer ??= Comparer<TKey>.Default;

            // Create a closure for this specific sort level
            int Compare(TElement a, TElement b)
            {
                var keyA = keySelector(a);
                var keyB = keySelector(b);
                var result = comparer.Compare(keyA, keyB);
                return descending ? -result : result;
            }

            // Copy existing comparers and add the new one
            var newComparers = new List<Func<TElement, TElement, int>>(_comparers);
            newComparers.Add(Compare);

            return new OrderedEnumerable<TElement>(_source, newComparers);
        }

        public IEnumerator<TElement> GetEnumerator()
        {
            // 1. Materialize source
            var buffer = new List<TElement>(_source);

            // 2. Sort using all comparers
            if (_comparers.Count > 0)
            {
                buffer.Sort((a, b) =>
                {
                    foreach (var comp in _comparers)
                    {
                        int res = comp(a, b);
                        if (res != 0) return res;
                    }
                    return 0;
                });
            }

            // 3. Return the iterator of the sorted list
            return buffer.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
