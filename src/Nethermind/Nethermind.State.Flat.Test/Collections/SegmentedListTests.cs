// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.State.Flat.Collections;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Collections;

[TestFixture]
public class SegmentedListTests
{
    // 256 is the segment size, so these counts straddle segment boundaries (partial, exact, multi-segment).
    [TestCase(1)]
    [TestCase(256)]
    [TestCase(257)]
    [TestCase(600)]
    public void Indexer_ReadsAndWritesAcrossSegments(int count)
    {
        using SegmentedList<int> list = new(clearOnReturn: false);
        list.EnsureCapacity(count);
        Assert.That(list.Capacity, Is.GreaterThanOrEqualTo(count));

        for (int i = 0; i < count; i++) list[i] = i + 1;
        for (int i = 0; i < count; i++) Assert.That(list[i], Is.EqualTo(i + 1));
    }

    [Test]
    public void EnsureCapacity_FreshSegmentsAreClearedAndGrowthRetainsLowerSegments()
    {
        using SegmentedList<int> list = new(clearOnReturn: false);
        list.EnsureCapacity(600);
        for (int i = 0; i < 600; i++) Assert.That(list[i], Is.EqualTo(0), $"index {i} should be default after allocation");

        for (int i = 0; i < 600; i++) list[i] = i + 1;

        list.EnsureCapacity(1500); // adds segments beyond the existing ones
        for (int i = 0; i < 600; i++) Assert.That(list[i], Is.EqualTo(i + 1), $"index {i} should survive growth");
        for (int i = 600; i < 1500; i++) Assert.That(list[i], Is.EqualTo(0), $"index {i} should be default in new segments");

        list.EnsureCapacity(100); // shrinking request is a no-op; nothing is dropped
        Assert.That(list.Capacity, Is.GreaterThanOrEqualTo(1500));
        Assert.That(list[599], Is.EqualTo(600));
    }

    [TestCase(256)] // exact segment boundary
    [TestCase(300)] // partial last segment
    [TestCase(600)] // whole populated range
    public void Clear_ZeroesOnlyThePrefix(int clearLength)
    {
        using SegmentedList<int> list = new(clearOnReturn: false);
        list.EnsureCapacity(600);
        for (int i = 0; i < 600; i++) list[i] = i + 1;

        list.Clear(clearLength);

        for (int i = 0; i < clearLength; i++) Assert.That(list[i], Is.EqualTo(0), $"index {i} should be cleared");
        for (int i = clearLength; i < 600; i++) Assert.That(list[i], Is.EqualTo(i + 1), $"index {i} should be untouched");
    }

    public enum SortShape { Random, AllEqual, Reverse, Single, Empty }

    [TestCase(SortShape.Random, 600)]
    [TestCase(SortShape.Random, 257)]
    [TestCase(SortShape.AllEqual, 512)]
    [TestCase(SortShape.Reverse, 600)]
    [TestCase(SortShape.Single, 1)]
    [TestCase(SortShape.Empty, 0)]
    public void Sort_MatchesArraySort(SortShape shape, int count)
    {
        int[] data = BuildSortData(shape, count);

        using SegmentedList<int> list = new(clearOnReturn: false);
        list.EnsureCapacity(Math.Max(count, 1));
        for (int i = 0; i < count; i++) list[i] = data[i];

        list.Sort(count, Comparer<int>.Default);

        int[] expected = (int[])data.Clone();
        Array.Sort(expected);
        for (int i = 0; i < count; i++) Assert.That(list[i], Is.EqualTo(expected[i]), $"index {i}");
    }

    [Test]
    public void Sort_HonoursCustomComparer_DescendingOrder()
    {
        int[] data = BuildSortData(SortShape.Random, 600);
        using SegmentedList<int> list = new(clearOnReturn: false);
        list.EnsureCapacity(600);
        for (int i = 0; i < 600; i++) list[i] = data[i];

        IComparer<int> descending = Comparer<int>.Create(static (a, b) => b.CompareTo(a));
        list.Sort(600, descending);

        int[] expected = (int[])data.Clone();
        Array.Sort(expected, descending);
        for (int i = 0; i < 600; i++) Assert.That(list[i], Is.EqualTo(expected[i]), $"index {i}");
    }

    private static int[] BuildSortData(SortShape shape, int count)
    {
        int[] data = new int[count];
        Random random = new(count * 31 + (int)shape);
        for (int i = 0; i < count; i++)
        {
            data[i] = shape switch
            {
                SortShape.AllEqual => 42,
                SortShape.Reverse => count - i,
                _ => random.Next(),
            };
        }
        return data;
    }
}
