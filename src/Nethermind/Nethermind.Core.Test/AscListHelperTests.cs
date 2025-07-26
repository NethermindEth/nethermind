// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.Test;

[TestFixture(42, 3, 1)]
[TestFixture(43, 5, 2)]
[TestFixture(44, 10, 3)]
[TestFixture(45, 100, 10)]
[TestFixture(46, 1000, 50)]
[TestFixture(47, 10_000, 100)]
[TestFixture(48, 10_000, 1_000)]
[Parallelizable(ParallelScope.All)]
public class AscListHelperTests(int seed, int size, int delta)
{
    [Test]
    public void Union_Random()
    {
        var random = new Random(seed);
        int[] l1 = Generate(random, size, delta);
        int[] l2 = Generate(random, size, delta);

        Assert.That(
            AscListHelper.Union(l1, l2),
            Is.EqualTo(l1.Concat(l2).Distinct().Order())
        );
    }

    [Test]
    public void UnionAll_Random()
    {
        var count = Math.Min(5, size / 2);

        var random = new Random(seed);
        var ls = Enumerable.Range(0, count).Select(_ => Generate(random, size, delta)).ToArray();

        var union = new HashSet<int>(ls.SelectMany(l => l));
        Assert.That(
            AscListHelper.UnionAll(ls),
            Is.EqualTo(union.Order())
        );
    }

    [Test]
    public void Intersect_Random()
    {
        var random = new Random(seed);
        int[] l1 = Generate(random, size, delta);
        int[] l2 = Generate(random, size, delta);

        Assert.That(
            AscListHelper.Intersect(l1, l2),
            Is.EqualTo(l1.Intersect(l2).Order())
        );
    }

    [Test]
    public void IntersectAll_Random()
    {
        var count = Math.Min(5, size / 2);

        var random = new Random(seed);
        var ls = Enumerable.Range(0, count).Select(_ => Generate(random, size, delta)).ToArray();

        var intersection = new HashSet<int>(ls[0]);
        ls.Skip(1).ForEach(l => intersection.IntersectWith(l));

        Assert.That(
            AscListHelper.IntersectAll(ls),
            Is.EqualTo(intersection.Order())
        );
    }

    private static int[] Generate(Random random, int size, int delta)
    {
        size = random.Next(size / 2, size + 1);
        var res = new int[size];

        var p = 0;
        for (var i = 0; i < size; i++)
        {
            res[i] = p + random.Next(1, delta + 1);
            p = res[i];
        }

        return res;
    }
}
