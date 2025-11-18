// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System;
using System.Collections;
using System.Collections.Generic;

namespace Nethermind.CoreOfCore
{
    /// <summary>
    /// A lightweight, LINQ-like implementation that does not depend on System.Linq.
    /// Supports the most common operators and can be extended as needed.
    /// </summary>
    public static class Enumerable
    {
        // Helper for argument checks
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

            return WhereIterator(source, predicate);
        }

        private static IEnumerable<TSource> WhereIterator<TSource>(
            IEnumerable<TSource> source,
            Func<TSource, bool> predicate)
        {
            foreach (var item in source)
            {
                if (predicate(item))
                    yield return item;
            }
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

            return SelectIterator(source, selector);
        }

        private static IEnumerable<TResult> SelectIterator<TSource, TResult>(
            IEnumerable<TSource> source,
            Func<TSource, TResult> selector)
        {
            foreach (var item in source)
            {
                yield return selector(item);
            }
        }

        // ---------------------------------------------------------------------
        // SELECTMANY (simple version)
        // ---------------------------------------------------------------------

        public static IEnumerable<TResult> SelectMany<TSource, TResult>(
            this IEnumerable<TSource> source,
            Func<TSource, IEnumerable<TResult>> selector)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(selector, nameof(selector));

            return SelectManyIterator(source, selector);
        }

        private static IEnumerable<TResult> SelectManyIterator<TSource, TResult>(
            IEnumerable<TSource> source,
            Func<TSource, IEnumerable<TResult>> selector)
        {
            foreach (var item in source)
            {
                var inner = selector(item);
                if (inner == null) continue;

                foreach (var result in inner)
                    yield return result;
            }
        }

        // ---------------------------------------------------------------------
        // ANY
        // ---------------------------------------------------------------------

        public static bool Any<TSource>(this IEnumerable<TSource> source)
        {
            ThrowIfNull(source, nameof(source));

            foreach (var _ in source)
            {
                return true;
            }

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
                if (predicate(item))
                    return true;
            }

            return false;
        }

        // ---------------------------------------------------------------------
        // ALL
        // ---------------------------------------------------------------------

        public static bool All<TSource>(
            this IEnumerable<TSource> source,
            Func<TSource, bool> predicate)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(predicate, nameof(predicate));

            foreach (var item in source)
            {
                if (!predicate(item))
                    return false;
            }

            return true;
        }

        // ---------------------------------------------------------------------
        // COUNT
        // ---------------------------------------------------------------------

        public static int Count<TSource>(this IEnumerable<TSource> source)
        {
            ThrowIfNull(source, nameof(source));

            int count = 0;
            foreach (var _ in source)
            {
                checked { count++; }
            }

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
                if (predicate(item))
                {
                    checked { count++; }
                }
            }

            return count;
        }

        // ---------------------------------------------------------------------
        // FIRST / FIRSTORDEFAULT
        // ---------------------------------------------------------------------

        public static TSource First<TSource>(this IEnumerable<TSource> source)
        {
            ThrowIfNull(source, nameof(source));

            foreach (var item in source)
            {
                return item;
            }

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
                if (predicate(item))
                    return item;
            }

            throw new InvalidOperationException("Sequence contains no matching element.");
        }

        public static TSource FirstOrDefault<TSource>(this IEnumerable<TSource> source)
        {
            ThrowIfNull(source, nameof(source));

            foreach (var item in source)
            {
                return item;
            }

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
                if (predicate(item))
                    return item;
            }

            return default!;
        }

        // ---------------------------------------------------------------------
        // SINGLE / SINGLEORDEFAULT
        // ---------------------------------------------------------------------

        public static TSource Single<TSource>(this IEnumerable<TSource> source)
        {
            ThrowIfNull(source, nameof(source));

            bool found = false;
            TSource result = default!;

            foreach (var item in source)
            {
                if (found)
                    throw new InvalidOperationException("Sequence contains more than one element.");

                found = true;
                result = item;
            }

            if (!found)
                throw new InvalidOperationException("Sequence contains no elements.");

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
                if (!predicate(item))
                    continue;

                if (found)
                    throw new InvalidOperationException("Sequence contains more than one matching element.");

                found = true;
                result = item;
            }

            if (!found)
                throw new InvalidOperationException("Sequence contains no matching element.");

            return result;
        }

        public static TSource SingleOrDefault<TSource>(this IEnumerable<TSource> source)
        {
            ThrowIfNull(source, nameof(source));

            bool found = false;
            TSource result = default!;

            foreach (var item in source)
            {
                if (found)
                    throw new InvalidOperationException("Sequence contains more than one element.");

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
                if (!predicate(item))
                    continue;

                if (found)
                    throw new InvalidOperationException("Sequence contains more than one matching element.");

                found = true;
                result = item;
            }

            return result;
        }

        // ---------------------------------------------------------------------
        // CONTAINS
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
                if (comparer.Equals(item, value))
                    return true;
            }

            return false;
        }

        // ---------------------------------------------------------------------
        // DISTINCT
        // ---------------------------------------------------------------------

        public static IEnumerable<TSource> Distinct<TSource>(
            this IEnumerable<TSource> source)
        {
            return Distinct(source, EqualityComparer<TSource>.Default);
        }

        public static IEnumerable<TSource> Distinct<TSource>(
            this IEnumerable<TSource> source,
            IEqualityComparer<TSource> comparer)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(comparer, nameof(comparer));

            var set = new HashSet<TSource>(comparer);
            foreach (var item in source)
            {
                if (set.Add(item))
                    yield return item;
            }
        }

        // ---------------------------------------------------------------------
        // TAKE / SKIP
        // ---------------------------------------------------------------------

        public static IEnumerable<TSource> Take<TSource>(
            this IEnumerable<TSource> source,
            int count)
        {
            ThrowIfNull(source, nameof(source));
            if (count <= 0) yield break;

            int taken = 0;
            foreach (var item in source)
            {
                yield return item;
                taken++;

                if (taken >= count)
                    yield break;
            }
        }

        public static IEnumerable<TSource> Skip<TSource>(
            this IEnumerable<TSource> source,
            int count)
        {
            ThrowIfNull(source, nameof(source));
            if (count <= 0)
            {
                foreach (var item in source)
                    yield return item;

                yield break;
            }

            int skipped = 0;
            foreach (var item in source)
            {
                if (skipped < count)
                {
                    skipped++;
                    continue;
                }

                yield return item;
            }
        }

        // ---------------------------------------------------------------------
        // TOARRAY / TOLIST
        // ---------------------------------------------------------------------

        public static TSource[] ToArray<TSource>(this IEnumerable<TSource> source)
        {
            ThrowIfNull(source, nameof(source));

            var list = new List<TSource>();
            foreach (var item in source)
            {
                list.Add(item);
            }

            return list.ToArray();
        }

        public static List<TSource> ToList<TSource>(this IEnumerable<TSource> source)
        {
            ThrowIfNull(source, nameof(source));

            var list = new List<TSource>();
            foreach (var item in source)
            {
                list.Add(item);
            }

            return list;
        }

        // ---------------------------------------------------------------------
        // AGGREGATE (basic)
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

                    // If lengths differ → not equal
                    if (!moved1 || !moved2)
                        return moved1 == moved2;

                    // Compare elements
                    if (!comparer.Equals(e1.Current, e2.Current))
                        return false;
                }
            }
        }

        // ---------------------------------------------------------------------
// CONCAT
// ---------------------------------------------------------------------

        public static IEnumerable<TSource> Concat<TSource>(
            this IEnumerable<TSource> first,
            IEnumerable<TSource> second)
        {
            ThrowIfNull(first, nameof(first));
            ThrowIfNull(second, nameof(second));

            return ConcatIterator(first, second);
        }

        private static IEnumerable<TSource> ConcatIterator<TSource>(
            IEnumerable<TSource> first,
            IEnumerable<TSource> second)
        {
            foreach (var item in first)
                yield return item;

            foreach (var item in second)
                yield return item;
        }

        // ---------------------------------------------------------------------
// TOHASHSET
// ---------------------------------------------------------------------

        public static HashSet<TSource> ToHashSet<TSource>(
            this IEnumerable<TSource> source)
        {
            ThrowIfNull(source, nameof(source));

            var set = new HashSet<TSource>();
            foreach (var item in source)
                set.Add(item);

            return set;
        }

        public static HashSet<TSource> ToHashSet<TSource>(
            this IEnumerable<TSource> source,
            IEqualityComparer<TSource> comparer)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(comparer, nameof(comparer));

            var set = new HashSet<TSource>(comparer);
            foreach (var item in source)
                set.Add(item);

            return set;
        }

        // ---------------------------------------------------------------------
// EMPTY
// ---------------------------------------------------------------------

        private static class EmptyCache<T>
        {
            public static readonly T[] Instance = new T[0];
        }

        public static IEnumerable<T> Empty<T>()
        {
            return EmptyCache<T>.Instance;
        }


        // ---------------------------------------------------------------------
// MIN (double)
// ---------------------------------------------------------------------

        public static double Min(this IEnumerable<double> source)
        {
            ThrowIfNull(source, nameof(source));

            using (var e = source.GetEnumerator())
            {
                if (!e.MoveNext())
                    throw new InvalidOperationException("Sequence contains no elements.");

                double min = e.Current;

                while (e.MoveNext())
                {
                    double val = e.Current;
                    if (val < min)
                        min = val;
                }

                return min;
            }
        }

        // ---------------------------------------------------------------------
// LAST
// ---------------------------------------------------------------------

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

            if (!found)
                throw new InvalidOperationException("Sequence contains no elements.");

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

            if (!found)
                throw new InvalidOperationException("Sequence contains no matching element.");

            return result;
        }

        // ---------------------------------------------------------------------
// SUM (int)
// ---------------------------------------------------------------------

        public static int Sum(this IEnumerable<int> source)
        {
            ThrowIfNull(source, nameof(source));

            int sum = 0;
            checked
            {
                foreach (var item in source)
                    sum += item;
            }

            return sum;
        }

        public static long Sum(this IEnumerable<long> source)
        {
            ThrowIfNull(source, nameof(source));

            long sum = 0;
            checked
            {
                foreach (var item in source)
                    sum += item;
            }

            return sum;
        }

        public static double Sum(this IEnumerable<double> source)
        {
            ThrowIfNull(source, nameof(source));

            double sum = 0;
            foreach (var item in source)
                sum += item;

            return sum;
        }

        public static float Sum(this IEnumerable<float> source)
        {
            ThrowIfNull(source, nameof(source));

            float sum = 0;
            foreach (var item in source)
                sum += item;

            return sum;
        }

        public static decimal Sum(this IEnumerable<decimal> source)
        {
            ThrowIfNull(source, nameof(source));

            decimal sum = 0;
            foreach (var item in source)
                sum += item;

            return sum;
        }


        // ---------------------------------------------------------------------
// CAST
// ---------------------------------------------------------------------

        public static IEnumerable<TResult> Cast<TResult>(this IEnumerable source)
        {
            ThrowIfNull(source, nameof(source));
            return CastIterator<TResult>(source);
        }

        private static IEnumerable<TResult> CastIterator<TResult>(IEnumerable source)
        {
            foreach (var item in source)
            {
                yield return (TResult)item!; // runtime cast (same as LINQ)
            }
        }

        // ---------------------------------------------------------------------
// ORDERBYDESCENDING
// ---------------------------------------------------------------------

        public static IEnumerable<TSource> OrderByDescending<TSource, TKey>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector)
        {
            return OrderByDescending(source, keySelector, Comparer<TKey>.Default);
        }

        public static IEnumerable<TSource> OrderByDescending<TSource, TKey>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector,
            IComparer<TKey> comparer)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(keySelector, nameof(keySelector));
            ThrowIfNull(comparer, nameof(comparer));

            var list = new List<TSource>(source);
            list.Sort((a, b) => comparer.Compare(keySelector(b), keySelector(a)));
            return list;
        }

        // ---------------------------------------------------------------------
// TAKEWHILE
// ---------------------------------------------------------------------

        public static IEnumerable<TSource> TakeWhile<TSource>(
            this IEnumerable<TSource> source,
            Func<TSource, bool> predicate)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(predicate, nameof(predicate));

            return TakeWhileIterator(source, predicate);
        }

        private static IEnumerable<TSource> TakeWhileIterator<TSource>(
            IEnumerable<TSource> source,
            Func<TSource, bool> predicate)
        {
            foreach (var item in source)
            {
                if (!predicate(item))
                    yield break;

                yield return item;
            }
        }

        // ---------------------------------------------------------------------
// OFTYPE
// ---------------------------------------------------------------------

        public static IEnumerable<TResult> OfType<TResult>(this IEnumerable source)
        {
            ThrowIfNull(source, nameof(source));
            return OfTypeIterator<TResult>(source);
        }

        private static IEnumerable<TResult> OfTypeIterator<TResult>(IEnumerable source)
        {
            foreach (var item in source)
            {
                if (item is TResult result)
                    yield return result;
            }
        }


        // ---------------------------------------------------------------------
// RANGE
// ---------------------------------------------------------------------

        public static IEnumerable<int> Range(int start, int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), "Count cannot be negative.");

            // Prevent overflow
            long max = (long)start + (long)count - 1;
            if (max > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(count), "Range exceeds Int32 max value.");

            return RangeIterator(start, count);
        }

        private static IEnumerable<int> RangeIterator(int start, int count)
        {
            int end = start + count;
            for (int i = start; i < end; i++)
                yield return i;
        }

        // ---------------------------------------------------------------------
// ASENUMERABLE
// ---------------------------------------------------------------------

        public static IEnumerable<TSource> AsEnumerable<TSource>(this IEnumerable<TSource> source)
        {
            ThrowIfNull(source, nameof(source));
            return source;
        }


        // ---------------------------------------------------------------------
// ORDERBY
// ---------------------------------------------------------------------

        public static IEnumerable<TSource> OrderBy<TSource, TKey>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector)
        {
            return OrderBy(source, keySelector, Comparer<TKey>.Default);
        }

        public static IEnumerable<TSource> OrderBy<TSource, TKey>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector,
            IComparer<TKey> comparer)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(keySelector, nameof(keySelector));
            ThrowIfNull(comparer, nameof(comparer));

            var list = new List<TSource>(source);
            list.Sort((a, b) => comparer.Compare(keySelector(a), keySelector(b)));
            return list;
        }


        // ---------------------------------------------------------------------
// TODICTIONARY
// ---------------------------------------------------------------------

        // ---------------------------------------------------------------------
// TODICTIONARY
// ---------------------------------------------------------------------

        public static Dictionary<TKey, TElement> ToDictionary<TSource, TKey, TElement>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector,
            Func<TSource, TElement> elementSelector
        )
            where TKey : notnull
        {
            return ToDictionary(source, keySelector, elementSelector, null);
        }

        public static Dictionary<TKey, TElement> ToDictionary<TSource, TKey, TElement>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector,
            Func<TSource, TElement> elementSelector,
            IEqualityComparer<TKey>? comparer
        )
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
            Func<TSource, TKey> keySelector
        )
            where TKey : notnull
        {
            return ToDictionary(source, keySelector, x => x, null);
        }
        // ---------------------------------------------------------------------
        // GROUPBY
        // ---------------------------------------------------------------------

        public static IEnumerable<IGrouping<TKey, TSource>> GroupBy<TSource, TKey>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector
        )
            where TKey : notnull
        {
            return GroupBy(source, keySelector, x => x, null);
        }

        public static IEnumerable<IGrouping<TKey, TSource>> GroupBy<TSource, TKey>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector,
            IEqualityComparer<TKey>? comparer
        )
            where TKey : notnull
        {
            return GroupBy(source, keySelector, x => x, comparer);
        }

        public static IEnumerable<IGrouping<TKey, TElement>> GroupBy<TSource, TKey, TElement>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector,
            Func<TSource, TElement> elementSelector
        )
            where TKey : notnull
        {
            return GroupBy(source, keySelector, elementSelector, null);
        }

        public static IEnumerable<IGrouping<TKey, TElement>> GroupBy<TSource, TKey, TElement>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector,
            Func<TSource, TElement> elementSelector,
            IEqualityComparer<TKey>? comparer
        )
            where TKey : notnull
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(keySelector, nameof(keySelector));
            ThrowIfNull(elementSelector, nameof(elementSelector));

            return GroupByIterator(source, keySelector, elementSelector, comparer);
        }

        private static IEnumerable<IGrouping<TKey, TElement>> GroupByIterator<TSource, TKey, TElement>(
            IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector,
            Func<TSource, TElement> elementSelector,
            IEqualityComparer<TKey>? comparer
        )
            where TKey : notnull
        {
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

            foreach (var kvp in groups)
            {
                yield return new Grouping<TKey, TElement>(kvp.Key, kvp.Value);
            }
        }

        // ---------------------------------------------------------------------
// DISTINCTBY
// ---------------------------------------------------------------------

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

            return DistinctByIterator(source, keySelector, comparer);
        }

        private static IEnumerable<TSource> DistinctByIterator<TSource, TKey>(
            IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector,
            IEqualityComparer<TKey>? comparer)
        {
            var seen = new HashSet<TKey>(comparer);

            foreach (var item in source)
            {
                var key = keySelector(item);
                if (seen.Add(key))
                    yield return item;
            }
        }

        // ---------------------------------------------------------------------
// REVERSE
// ---------------------------------------------------------------------

        public static IEnumerable<TSource> Reverse<TSource>(
            this IEnumerable<TSource> source)
        {
            ThrowIfNull(source, nameof(source));
            return ReverseIterator(source);
        }

        private static IEnumerable<TSource> ReverseIterator<TSource>(
            IEnumerable<TSource> source)
        {
            var list = new List<TSource>(source);
            for (int i = list.Count - 1; i >= 0; i--)
                yield return list[i];
        }

        // ---------------------------------------------------------------------
// ZIP
// ---------------------------------------------------------------------

        public static IEnumerable<TResult> Zip<TFirst, TSecond, TResult>(
            this IEnumerable<TFirst> first,
            IEnumerable<TSecond> second,
            Func<TFirst, TSecond, TResult> resultSelector)
        {
            ThrowIfNull(first, nameof(first));
            ThrowIfNull(second, nameof(second));
            ThrowIfNull(resultSelector, nameof(resultSelector));

            return ZipIterator(first, second, resultSelector);
        }

        private static IEnumerable<TResult> ZipIterator<TFirst, TSecond, TResult>(
            IEnumerable<TFirst> first,
            IEnumerable<TSecond> second,
            Func<TFirst, TSecond, TResult> resultSelector)
        {
            using (var e1 = first.GetEnumerator())
            using (var e2 = second.GetEnumerator())
            {
                while (e1.MoveNext() && e2.MoveNext())
                {
                    yield return resultSelector(e1.Current, e2.Current);
                }
            }
        }


        // ---------------------------------------------------------------------
// UNION
// ---------------------------------------------------------------------

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

            return UnionIterator(first, second, comparer);
        }

        private static IEnumerable<TSource> UnionIterator<TSource>(
            IEnumerable<TSource> first,
            IEnumerable<TSource> second,
            IEqualityComparer<TSource>? comparer)
        {
            var set = new HashSet<TSource>(comparer);

            foreach (var item in first)
            {
                if (set.Add(item))
                    yield return item;
            }

            foreach (var item in second)
            {
                if (set.Add(item))
                    yield return item;
            }
        }


        public static IEnumerable<TSource> SkipWhile<TSource>(
            this IEnumerable<TSource> source,
            Func<TSource, int, bool> predicate)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(predicate, nameof(predicate));

            return SkipWhileIterator(source, predicate);
        }

        private static IEnumerable<TSource> SkipWhileIterator<TSource>(
            IEnumerable<TSource> source,
            Func<TSource, int, bool> predicate)
        {
            bool yielding = false;
            int index = 0;

            foreach (var item in source)
            {
                if (!yielding)
                {
                    if (!predicate(item, index))
                        yielding = true;
                    else
                    {
                        index++;
                        continue;
                    }
                }

                yield return item;
                index++;
            }
        }


        // ---------------------------------------------------------------------
// REPEAT
// ---------------------------------------------------------------------

        public static IEnumerable<TSource> Repeat<TSource>(TSource element, int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), "Count must be non-negative.");

            return RepeatIterator(element, count);
        }

        private static IEnumerable<TSource> RepeatIterator<TSource>(TSource element, int count)
        {
            for (int i = 0; i < count; i++)
                yield return element;
        }


        // ---------------------------------------------------------------------
// SELECT (indexed)
// ---------------------------------------------------------------------

        public static IEnumerable<TResult> Select<TSource, TResult>(
            this IEnumerable<TSource> source,
            Func<TSource, int, TResult> selector)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(selector, nameof(selector));

            return SelectIterator(source, selector);
        }

        private static IEnumerable<TResult> SelectIterator<TSource, TResult>(
            IEnumerable<TSource> source,
            Func<TSource, int, TResult> selector)
        {
            int index = 0;
            foreach (var item in source)
            {
                yield return selector(item, index);
                index++;
            }
        }

        // ---------------------------------------------------------------------
// SKIPLAST
// ---------------------------------------------------------------------

        public static IEnumerable<TSource> SkipLast<TSource>(
            this IEnumerable<TSource> source,
            int count)
        {
            ThrowIfNull(source, nameof(source));

            if (count <= 0)
                return source; // no skipping needed

            return SkipLastIterator(source, count);
        }

        private static IEnumerable<TSource> SkipLastIterator<TSource>(
            IEnumerable<TSource> source,
            int count)
        {
            // Efficient: only keeps up to <count> items in memory.
            var buffer = new Queue<TSource>(count + 1);

            foreach (var item in source)
            {
                buffer.Enqueue(item);

                if (buffer.Count > count)
                    yield return buffer.Dequeue();
            }
        }


        // ---------------------------------------------------------------------
// ZIP (indexed overload)
// ---------------------------------------------------------------------

        public static IEnumerable<TResult> Zip<TSource, TResult>(
            this IEnumerable<TSource> source,
            Func<TSource, int, TResult> resultSelector)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(resultSelector, nameof(resultSelector));

            return ZipIterator(source, resultSelector);
        }

        private static IEnumerable<TResult> ZipIterator<TSource, TResult>(
            IEnumerable<TSource> source,
            Func<TSource, int, TResult> resultSelector)
        {
            int index = 0;
            foreach (var item in source)
                yield return resultSelector(item, index++);
        }


        // ---------------------------------------------------------------------
// ZIP (pair → tuple)
// ---------------------------------------------------------------------

        public static IEnumerable<(TFirst First, TSecond Second)> Zip<TFirst, TSecond>(
            this IEnumerable<TFirst> first,
            IEnumerable<TSecond> second)
        {
            ThrowIfNull(first, nameof(first));
            ThrowIfNull(second, nameof(second));

            return ZipIterator(first, second);
        }

        private static IEnumerable<(TFirst First, TSecond Second)> ZipIterator<TFirst, TSecond>(
            IEnumerable<TFirst> first,
            IEnumerable<TSecond> second)
        {
            using var e1 = first.GetEnumerator();
            using var e2 = second.GetEnumerator();

            while (e1.MoveNext() && e2.MoveNext())
                yield return (e1.Current, e2.Current);
        }

        // ---------------------------------------------------------------------
// SKIPWHILE (predicate only)
// ---------------------------------------------------------------------

        public static IEnumerable<TSource> SkipWhile<TSource>(
            this IEnumerable<TSource> source,
            Func<TSource, bool> predicate)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(predicate, nameof(predicate));

            return SkipWhileIterator(source, predicate);
        }

        private static IEnumerable<TSource> SkipWhileIterator<TSource>(
            IEnumerable<TSource> source,
            Func<TSource, bool> predicate)
        {
            bool yielding = false;

            foreach (var item in source)
            {
                if (!yielding && !predicate(item))
                    yielding = true;

                if (yielding)
                    yield return item;
            }
        }


        // ---------------------------------------------------------------------
// LASTORDEFAULT (no predicate)
// ---------------------------------------------------------------------

        public static TSource? LastOrDefault<TSource>(
            this IEnumerable<TSource> source)
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

// ---------------------------------------------------------------------
// LASTORDEFAULT (with predicate)
// ---------------------------------------------------------------------

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
// CHUNK
// ---------------------------------------------------------------------

        public static IEnumerable<TSource[]> Chunk<TSource>(
            this IEnumerable<TSource> source,
            int size)
        {
            ThrowIfNull(source, nameof(source));
            if (size <= 0)
                throw new ArgumentOutOfRangeException(nameof(size));

            return ChunkIterator(source, size);
        }

        private static IEnumerable<TSource[]> ChunkIterator<TSource>(
            IEnumerable<TSource> source,
            int size)
        {
            using var e = source.GetEnumerator();

            while (true)
            {
                var chunk = new TSource[size];
                int i = 0;

                for (; i < size; i++)
                {
                    if (!e.MoveNext())
                        break;

                    chunk[i] = e.Current;
                }

                if (i == 0)
                    yield break;

                if (i < size)
                    Array.Resize(ref chunk, i);

                yield return chunk;
            }
        }


        // ---------------------------------------------------------------------
// SUM with selector (long)
// ---------------------------------------------------------------------

        public static long Sum<TSource>(
            this IEnumerable<TSource> source,
            Func<TSource, long> selector)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(selector, nameof(selector));

            long sum = 0;
            checked
            {
                foreach (var item in source)
                    sum += selector(item);
            }
            return sum;
        }

        // ---------------------------------------------------------------------
// THENBYDESCENDING
// ---------------------------------------------------------------------

        public static IOrderedEnumerable<TSource> ThenByDescending<TSource, TKey>(
            this IOrderedEnumerable<TSource> source,
            Func<TSource, TKey> keySelector)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (keySelector is null) throw new ArgumentNullException(nameof(keySelector));

            return source.CreateOrderedEnumerable(
                keySelector,
                Comparer<TKey>.Default,
                descending: true);
        }


        // ---------------------------------------------------------------------
// FIRSTORDEFAULT(predicate, defaultValue)
// ---------------------------------------------------------------------

        public static TSource FirstOrDefault<TSource>(
            this IEnumerable<TSource> source,
            Func<TSource, bool> predicate,
            TSource defaultValue)
        {
            ThrowIfNull(source, nameof(source));
            ThrowIfNull(predicate, nameof(predicate));

            foreach (var item in source)
                if (predicate(item))
                    return item;

            return defaultValue;
        }


    }


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
}
