// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Nethermind.Core;

/// <summary>
/// Helper class for fast operations with strictly increasing lists of integers.
/// </summary>
/// <remarks>
/// Doesn't verify that parameters satisfy order requirement. <br/>
/// Can reuse parameters as return values to minimize allocations.
/// </remarks>
// TODO: optimize memory usage/copying in *All methods?
public static class AscListHelper
{
    public static TList IntersectTo<T, TList>(TList destination, IList<T> source1, IList<T> source2)
        where T: IComparable<T>
        where TList : IList<T>
    {
        var i = 0;
        var j = 0;

        while (i < source1.Count && j < source2.Count)
        {
            switch (source1[i].CompareTo(source2[j]))
            {
                case -1:
                    i++;
                    continue;
                case 1:
                    j++;
                    continue;
                default:
                    destination.Add(source1[i++]);
                    j++;
                    continue;
            }
        }

        return destination;
    }

    public static IList<T> Intersect<T>(IList<T> source1, IList<T> source2)
        where T: IComparable<T>
    {
        if (source1.Count == 0 || source2.Count == 0)
            return [];

        var destination = new List<T>(Math.Min(source1.Count, source2.Count));
        return IntersectTo(destination, source1, source2);
    }

    public static TList UnionTo<T, TList>(TList destination, IList<T> source1, IList<T> source2)
        where T: IComparable<T>
        where TList : IList<T>
    {
        var i = 0;
        var j = 0;

        while (i < source1.Count && j < source2.Count)
        {
            switch (source1[i].CompareTo(source2[j]))
            {
                case -1:
                    destination.Add(source1[i++]);
                    continue;
                case 1:
                    destination.Add(source2[j++]);
                    continue;
                default:
                    destination.Add(source1[i++]);
                    j++;
                    continue;
            }
        }

        for (var k = i; k < source1.Count; k++)
            destination.Add(source1[k]);

        for (var k = j; k < source2.Count; k++)
            destination.Add(source2[k]);

        return destination;
    }

    public static IList<T> Union<T>(IList<T> source1, IList<T> source2)
        where T: IComparable<T>
    {
        if (source1.Count == 0) return source2;
        if (source2.Count == 0) return source1;

        var destination = new List<T>(Math.Max(source1.Count, source2.Count));
        return UnionTo(destination, source1, source2);
    }

    public static IList<T> IntersectAll<T>(IEnumerable<IList<T>> sources)
        where T: IComparable<T> =>
        sources.Aggregate<IList<T>, IList<T>?>(null, (current, l) => current is null ? l.ToList() : Intersect(current, l)) ?? [];

    public static IList<T> IntersectAll<T>(ICollection<List<T>> sources)
        where T: IComparable<T> =>
        sources.Count == 1 ? sources.First() : IntersectAll(sources.AsEnumerable());

    public static IList<T> UnionAll<T>(IEnumerable<IList<T>> sources)
        where T: IComparable<T> =>
        sources.Aggregate<IList<T>, IList<T>?>(null, (current, l) => current is null ? l.ToList() : Union(current, l)) ?? [];

    public static IList<T> UnionAll<T>(ICollection<List<T>> sources)
        where T: IComparable<T> =>
        sources.Count == 1 ? sources.First() : UnionAll(sources.AsEnumerable());

    // TODO: remove?
    public static bool IsStrictlyAscendingNum<T>(IList<T> source)
        where T : INumber<T>
    {
        int j = source.Count - 1;
        if (j < 1) return true;

        T ai = source[0];
        var i = 1;

        while (i <= j && ai < (ai = T.CreateChecked(source[i]))) i++;
        return i > j;
    }

    public static bool IsStrictlyAscending<T>(IList<T> source)
        where T : IComparable<T>
    {
        if (source.Count == 0) return true;
        T prev = source[0];

        for (var i = 1; i < source.Count; i++)
            if (prev.CompareTo(source[i]) >= 0) return false;

        return true;
    }
}
