// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;

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
    public static T IntersectTo<T>(T destination, IReadOnlyList<int> source1, IReadOnlyList<int> source2)
        where T : IList<int>
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

    public static List<int> Intersect(IReadOnlyList<int> source1, IReadOnlyList<int> source2)
    {
        var destination = new List<int>(Math.Min(source1.Count, source2.Count));
        return IntersectTo(destination, source1, source2);
    }

    public static List<int> Intersect(List<int> source1, List<int> source2)
    {
        if (source1.Count == 0) return source2;
        if (source2.Count == 0) return source1;
        return Intersect((IReadOnlyList<int>)source1, source2);
    }

    public static T UnionTo<T>(T destination, IReadOnlyList<int> source1, IReadOnlyList<int> source2)
        where T : IList<int>
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

    public static List<int> Union(IReadOnlyList<int> source1, IReadOnlyList<int> source2)
    {
        var destination = new List<int>(Math.Max(source1.Count, source2.Count));
        return UnionTo(destination, source1, source2);
    }

    public static List<int> Union(List<int> source1, List<int> source2)
    {
        if (source1.Count == 0) return source2;
        if (source2.Count == 0) return source1;
        return Union((IReadOnlyList<int>)source1, source2);
    }

    public static List<int> IntersectAll(IEnumerable<IReadOnlyList<int>> sources) =>
        sources.Aggregate<IReadOnlyList<int>, List<int>?>(null, (current, l) => current is null ? l.ToList() : Intersect(current, l)) ?? [];

    public static List<int> IntersectAll(ICollection<List<int>> sources) =>
        sources.Count == 1 ? sources.First() : IntersectAll(sources.AsEnumerable());

    public static List<int> UnionAll(IEnumerable<IReadOnlyList<int>> sources) =>
        sources.Aggregate<IReadOnlyList<int>, List<int>?>(null, (current, l) => current is null ? l.ToList() : Union(current, l)) ?? [];

    public static List<int> UnionAll(ICollection<List<int>> sources) =>
        sources.Count == 1 ? sources.First() : UnionAll(sources.AsEnumerable());

    public static bool IsStrictlyAscending(IReadOnlyList<int> source)
    {
        int j = source.Count - 1;
        if (j < 1) return true;
        int ai = source[0], i = 1;
        while (i <= j && ai < (ai = source[i])) i++;
        return i > j;
    }
}
