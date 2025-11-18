// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System;
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
    }
}
