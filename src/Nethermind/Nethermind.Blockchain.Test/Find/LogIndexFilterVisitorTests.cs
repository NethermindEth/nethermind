// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Facade.Find;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Find;

// TODO!: tests for enumerator disposal
public class LogIndexFilterVisitorTests
{
    [TestCase(
        new[] { 1, 3, 5, 7, 9, },
        new[] { 0, 2, 4, 6, 8 },
        TestName = "Non-intersecting, but similar ranges"
    )]
    [TestCase(
        new[] { 1, 2, 3, 4, 5, },
        new[] { 5, 6, 7, 8, 9 },
        TestName = "Intersects on first/last"
    )]
    [TestCase(
        new[] { 1, 2, 3, 4, 5, },
        new[] { 6, 7, 8, 9, 10 },
        TestName = "Non-intersecting ranges"
    )]
    public void IntersectEnumerator(int[] s1, int[] s2)
    {
        var expected = s1.Intersect(s2).Order().ToArray();

        Verify<LogIndexFilterVisitor.IntersectEnumerator>(s1, s2, expected);
        Verify<LogIndexFilterVisitor.IntersectEnumerator>(s2, s1, expected);
    }

    [TestCase(1, 1)]
    [TestCase(20, 20)]
    [TestCase(20, 100)]
    [TestCase(100, 100)]
    [TestCase(1000, 1000)]
    public void IntersectEnumerator_Random(int len1, int len2)
    {
        var random = new Random(42);
        var s1 = RandomAscending(random, len1, Math.Max(1, len1 / 10));
        var s2 = RandomAscending(random, len2, Math.Max(1, len2 / 10));

        var expected = s1.Intersect(s2).Order().ToArray();
        Assert.That(expected, Is.Not.Empty, "Unreliable test: Needs non-empty sequence to verify against.");

        Verify<LogIndexFilterVisitor.IntersectEnumerator>(s1, s2, expected);
        Verify<LogIndexFilterVisitor.IntersectEnumerator>(s2, s1, expected);
    }

    [TestCase(0, 0)]
    [TestCase(0, 1)]
    [TestCase(0, 10)]
    public void IntersectEnumerator_SomeEmpty(int len1, int len2)
    {
        var s1 = Enumerable.Range(0, len1).ToArray();
        var s2 = Enumerable.Range(0, len2).ToArray();

        Verify<LogIndexFilterVisitor.IntersectEnumerator>(s1, s2, []);
        Verify<LogIndexFilterVisitor.IntersectEnumerator>(s2, s1, []);
    }

    [TestCase(
        new[] { 1, 2, 3, 4, 5, },
        new[] { 2, 3, 4 },
        TestName = "Contained"
    )]
    [TestCase(
        new[] { 1, 2, 3, 4, 5, },
        new[] { 1, 2, 3, 4, 5, },
        TestName = "Identical"
    )]
    [TestCase(
        new[] { 1, 3, 5, 7, 9, },
        new[] { 2, 4, 6, 8, 10 },
        TestName = "Complementary"
    )]
    public void UnionEnumerator(int[] s1, int[] s2)
    {
        var expected = s1.Union(s2).Distinct().Order().ToArray();

        Verify<LogIndexFilterVisitor.UnionEnumerator>(s1, s2, expected);
        Verify<LogIndexFilterVisitor.UnionEnumerator>(s2, s1, expected);
    }

    [TestCase(1, 1)]
    [TestCase(20, 20)]
    [TestCase(20, 100)]
    [TestCase(100, 100)]
    [TestCase(1000, 1000)]
    public void UnionEnumerator_Random(int len1, int len2)
    {
        var random = new Random(42);
        var s1 = RandomAscending(random, len1, Math.Max(1, len1 / 10));
        var s2 = RandomAscending(random, len2, Math.Max(1, len2 / 10));

        var expected = s1.Union(s2).Distinct().Order().ToArray();

        Verify<LogIndexFilterVisitor.UnionEnumerator>(s1, s2, expected);
        Verify<LogIndexFilterVisitor.UnionEnumerator>(s2, s1, expected);
    }

    [TestCase(0, 0)]
    [TestCase(0, 1)]
    [TestCase(0, 10)]
    public void UnionEnumerator_SomeEmpty(int len1, int len2)
    {
        var s1 = Enumerable.Range(0, len1).ToArray();
        var s2 = Enumerable.Range(0, len2).ToArray();

        var expected = s1.Union(s2).Distinct().Order().ToArray();

        Verify<LogIndexFilterVisitor.UnionEnumerator>(s1, s2, expected);
        Verify<LogIndexFilterVisitor.UnionEnumerator>(s2, s1, expected);
    }

    private static int[] RandomAscending(Random random, int count, int maxDelta)
    {
        var result = new int[count];

        for (var i = 0; i < result.Length; i++)
        {
            var min = i > 0 ? result[i - 1] : -1;
            result[i] = random.Next(min + 1, min + 1 + maxDelta);
        }

        return result;
    }

    private static void Verify<T>(int[] s1, int[] s2, int[] ex)
        where T: IEnumerator<int>
    {
        using var enumerator = (T)Activator.CreateInstance(
            typeof(T),
            s1.Cast<int>().GetEnumerator(),
            s2.Cast<int>().GetEnumerator()
        )!;

        Assert.That(Enumerate(enumerator), Is.EqualTo(ex));
    }

    private static IEnumerable<int> Enumerate(IEnumerator<int> enumerator)
    {
        while(enumerator.MoveNext())
            yield return enumerator.Current;
    }
}
