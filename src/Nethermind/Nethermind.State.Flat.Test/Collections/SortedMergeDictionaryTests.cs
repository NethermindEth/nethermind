// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.State.Flat.Collections;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Collections;

[TestFixture]
public class SortedMergeDictionaryTests
{
    private static readonly IComparer<int> Cmp = Comparer<int>.Default;

    [Test]
    public void FromUnsorted_LooksUpEveryKey_AndIteratesSorted()
    {
        Dictionary<int, int> source = [];
        for (int i = 0; i < 500; i++) source[i * 7 % 500] = i;

        SortedMergeDictionary<int, int> dict = SortedMergeDictionary<int, int>.FromUnsorted(source, Cmp);

        Assert.That(dict.Count, Is.EqualTo(source.Count));
        foreach (KeyValuePair<int, int> kv in source)
        {
            Assert.That(dict.TryGetValue(kv.Key, out int value), Is.True);
            Assert.That(value, Is.EqualTo(kv.Value));
        }
        Assert.That(dict.TryGetValue(-1, out _), Is.False);
        Assert.That(dict.TryGetValue(10_000, out _), Is.False);

        List<int> keys = dict.Select(static kv => kv.Key).ToList();
        Assert.That(keys, Is.Ordered);
        Assert.That(keys, Is.EquivalentTo(source.Keys));
    }

    [TestCase(0)]
    [TestCase(1)]
    public void EdgeCases_EmptyAndSingleEntry(int count)
    {
        Dictionary<int, int> source = [];
        for (int i = 0; i < count; i++) source[i] = i + 42;

        SortedMergeDictionary<int, int> dict = SortedMergeDictionary<int, int>.FromUnsorted(source, Cmp);

        Assert.That(dict.Count, Is.EqualTo(count));
        Assert.That(dict.TryGetValue(0, out int value), Is.EqualTo(count == 1));
        if (count == 1) Assert.That(value, Is.EqualTo(42));
    }

    [Test]
    public void Merge_DisjointSources_ProducesSortedUnion()
    {
        SortedMergeDictionary<int, int> a = FromPairs((1, 10), (3, 30), (5, 50));
        SortedMergeDictionary<int, int> b = FromPairs((2, 20), (4, 40), (6, 60));

        SortedMergeDictionary<int, int> merged = SortedMergeDictionary<int, int>.Merge([a, b], Cmp);

        Assert.That(merged.Select(static kv => kv.Key), Is.EqualTo(new[] { 1, 2, 3, 4, 5, 6 }));
        Assert.That(merged.Select(static kv => kv.Value), Is.EqualTo(new[] { 10, 20, 30, 40, 50, 60 }));
    }

    [Test]
    public void Merge_OverlappingKeys_HighestPrioritySourceWins()
    {
        // Sources in ascending priority: later index overrides on equal keys.
        SortedMergeDictionary<int, int> oldest = FromPairs((1, 100), (2, 100), (3, 100));
        SortedMergeDictionary<int, int> middle = FromPairs((2, 200), (4, 200));
        SortedMergeDictionary<int, int> newest = FromPairs((3, 300), (4, 300), (5, 300));

        SortedMergeDictionary<int, int> merged = SortedMergeDictionary<int, int>.Merge([oldest, middle, newest], Cmp);

        Assert.That(merged.Count, Is.EqualTo(5));
        AssertValue(merged, 1, 100); // only in oldest
        AssertValue(merged, 2, 200); // oldest + middle -> middle
        AssertValue(merged, 3, 300); // oldest + newest -> newest
        AssertValue(merged, 4, 300); // middle + newest -> newest
        AssertValue(merged, 5, 300); // only in newest
        Assert.That(merged.Select(static kv => kv.Key), Is.Ordered);
    }

    [Test]
    public void Merge_SingleAndEmptySources()
    {
        SortedMergeDictionary<int, int> only = FromPairs((7, 70), (8, 80));
        Assert.That(SortedMergeDictionary<int, int>.Merge([only], Cmp).Count, Is.EqualTo(2));

        SortedMergeDictionary<int, int> empty = FromPairs();
        SortedMergeDictionary<int, int> merged = SortedMergeDictionary<int, int>.Merge([empty, only, empty], Cmp);
        Assert.That(merged.Count, Is.EqualTo(2));
        AssertValue(merged, 7, 70);
    }

    [TestCase(2, 50)]
    [TestCase(3, 200)]
    [TestCase(8, 500)]
    [TestCase(16, 1000)]
    public void Merge_RandomizedAgainstReference(int sourceCount, int keySpace)
    {
        Random random = new(sourceCount * 31 + keySpace);

        List<SortedMergeDictionary<int, int>> sources = new(sourceCount);
        Dictionary<int, int> reference = [];
        for (int s = 0; s < sourceCount; s++)
        {
            // Each source is a random subset of the key space; value encodes (key, source) so priority is checkable.
            SortedDictionary<int, int> sorted = [];
            int entries = random.Next(keySpace / 2, keySpace);
            for (int e = 0; e < entries; e++)
            {
                int key = random.Next(keySpace);
                int value = key * 100 + s;
                sorted[key] = value;
                reference[key] = value; // reference applies sources in the same ascending order -> last wins
            }
            sources.Add(SortedMergeDictionary<int, int>.FromUnsorted(sorted, Cmp));
        }

        SortedMergeDictionary<int, int> merged = SortedMergeDictionary<int, int>.Merge(sources.ToArray(), Cmp);

        Assert.That(merged.Count, Is.EqualTo(reference.Count));
        List<int> keys = merged.Select(static kv => kv.Key).ToList();
        Assert.That(keys, Is.Ordered);
        Assert.That(keys, Is.Unique);
        foreach (KeyValuePair<int, int> kv in reference)
        {
            Assert.That(merged.TryGetValue(kv.Key, out int value), Is.True, $"missing key {kv.Key}");
            Assert.That(value, Is.EqualTo(kv.Value), $"wrong priority for key {kv.Key}");
        }
    }

    [Test]
    public void NoResizeClear_ThenRebuild_ReflectsNewDataAndReusesInstance()
    {
        using SortedMergeDictionary<int, int> dict = new();

        Dictionary<int, int> first = [];
        for (int i = 0; i < 300; i++) first[i] = i * 10;
        dict.BuildFromUnsorted(first, Cmp);
        Assert.That(dict.Count, Is.EqualTo(300));
        Assert.That(dict.TryGetValue(5, out int v1), Is.True);
        Assert.That(v1, Is.EqualTo(50));

        dict.NoResizeClear();
        Assert.That(dict.Count, Is.EqualTo(0));
        Assert.That(dict.TryGetValue(5, out _), Is.False);

        // Rebuild the same instance (arrays reused) via a merge with disjoint, smaller data.
        SortedMergeDictionary<int, int> a = FromPairs((1000, 1), (1002, 1));
        SortedMergeDictionary<int, int> b = FromPairs((1001, 2), (1002, 2));
        dict.BuildFromMerge([a, b], Cmp);

        Assert.That(dict.Count, Is.EqualTo(3));
        Assert.That(dict.TryGetValue(5, out _), Is.False);       // old data gone
        Assert.That(dict.TryGetValue(1001, out int v2), Is.True);
        Assert.That(v2, Is.EqualTo(2));
        Assert.That(dict.TryGetValue(1002, out int v3), Is.True);
        Assert.That(v3, Is.EqualTo(2));                          // newest source wins
        Assert.That(dict.Select(static kv => kv.Key), Is.EqualTo(new[] { 1000, 1001, 1002 }));
    }

    private static SortedMergeDictionary<int, int> FromPairs(params (int Key, int Value)[] pairs)
    {
        Dictionary<int, int> source = [];
        foreach ((int key, int value) in pairs) source[key] = value;
        return SortedMergeDictionary<int, int>.FromUnsorted(source, Cmp);
    }

    private static void AssertValue(SortedMergeDictionary<int, int> dict, int key, int expected)
    {
        Assert.That(dict.TryGetValue(key, out int value), Is.True, $"missing key {key}");
        Assert.That(value, Is.EqualTo(expected), $"key {key}");
    }
}
