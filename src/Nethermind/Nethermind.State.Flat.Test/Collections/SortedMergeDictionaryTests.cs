// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Nethermind.State.Flat.Collections;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Collections;

[TestFixture]
public class SortedMergeDictionaryTests
{
    private static readonly IComparer<int> Cmp = Comparer<int>.Default;
    private static readonly FieldInfo BucketSaltField = typeof(SortedMergeDictionary<int, int>)
        .GetField("_bucketSalt", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo BucketMaskField = typeof(SortedMergeDictionary<int, int>)
        .GetField("_bucketMask", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo StringEntriesDirtyField = typeof(SortedMergeDictionary<string, string>)
        .GetField("_entriesDirty", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo StringEntriesField = typeof(SortedMergeDictionary<string, string>)
        .GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!;

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
        using SortedMergeDictionary<int, int> only = FromPairs((7, 70), (8, 80));
        using SortedMergeDictionary<int, int> single = SortedMergeDictionary<int, int>.Merge([only], Cmp);
        Assert.That(single.Count, Is.EqualTo(2));

        using SortedMergeDictionary<int, int> empty = FromPairs();
        using SortedMergeDictionary<int, int> merged = new();
        merged.BuildFromMerge([empty, only, empty], Cmp, static (source, key) => source == 1 && key == 7);
        Assert.That(merged.Count, Is.EqualTo(1));
        AssertValue(merged, 7, 70);
    }

    [TestCase(1, 50, true)]
    [TestCase(2, 50, true)]
    [TestCase(2, 50, false)]
    [TestCase(3, 200, false)]
    [TestCase(8, 500, false)]
    [TestCase(16, 1000, false)]
    [TestCase(256, 20, false)]
    public void Merge_RandomizedAgainstReference(int sourceCount, int keySpace, bool filter)
    {
        Random random = new(sourceCount * 31 + keySpace);
        static bool Keep(int source, int key) => key % 5 != 0 && (source != 1 || key % 3 != 0);

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
                if (!filter || Keep(s, key)) reference[key] = value;
            }
            sources.Add(SortedMergeDictionary<int, int>.FromUnsorted(sorted, Cmp));
        }

        SortedMergeDictionary<int, int> merged = SortedMergeDictionary<int, int>.Merge(
            sources.ToArray(), Cmp, filter ? Keep : null);

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
    public void Merge_RunSourcesMatchDictionarySources()
    {
        Dictionary<int, int> oldestData = new() { [4] = 10, [2] = 10, [1] = 10 };
        Dictionary<int, int> middleData = new() { [4] = 20, [3] = 20, [2] = 20 };
        Dictionary<int, int> newestData = new() { [5] = 30, [4] = 30, [2] = 30 };
        static bool Keep(int source, int key) => key != 3 && !(source == 2 && key == 2);

        using SortedMergeDictionary<int, int>.PooledRun oldestRun =
            SortedMergeDictionary<int, int>.BuildRunFromUnsorted(oldestData, Cmp);
        using SortedMergeDictionary<int, int>.PooledRun middleRun =
            SortedMergeDictionary<int, int>.BuildRunFromUnsorted(middleData, Cmp);
        using SortedMergeDictionary<int, int>.PooledRun newestRun =
            SortedMergeDictionary<int, int>.BuildRunFromUnsorted(newestData, Cmp);
        SortedMergeDictionary<int, int>.Run[] runs =
            [oldestRun.AsRun(), middleRun.AsRun(), newestRun.AsRun()];

        using SortedMergeDictionary<int, int> actual = new();
        actual.BuildFromMerge(runs, Cmp, Keep);

        KeyValuePair<int, int>[] expectedEntries =
        [
            new(1, 10),
            new(2, 20),
            new(4, 30),
            new(5, 30),
        ];
        Assert.That(actual.ToArray(), Is.EqualTo(expectedEntries));
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

    [Test]
    public void RepeatedReuse_StaleBucketsNeverResurrectOldKeys()
    {
        // Reuses one instance across builds of very different sizes; every key from a previous build must miss
        // even though the bucket array is reused without being rebuilt from scratch.
        using SortedMergeDictionary<int, int> dict = new();
        int[] sizes = [400, 3, 150, 1, 500, 0, 47];
        List<int> previousKeys = [];

        for (int cycle = 0; cycle < sizes.Length; cycle++)
        {
            Dictionary<int, int> source = [];
            int baseKey = cycle * 1_000_000;
            for (int i = 0; i < sizes[cycle]; i++) source[baseKey + i] = cycle;

            dict.NoResizeClear();
            dict.BuildFromUnsorted(source, Cmp);

            Assert.That(dict.Count, Is.EqualTo(sizes[cycle]));
            foreach (KeyValuePair<int, int> kv in source)
            {
                Assert.That(dict.TryGetValue(kv.Key, out int value), Is.True, $"cycle {cycle}: missing key {kv.Key}");
                Assert.That(value, Is.EqualTo(kv.Value));
            }
            foreach (int stale in previousKeys)
            {
                Assert.That(dict.TryGetValue(stale, out _), Is.False, $"cycle {cycle}: stale key {stale} resurrected");
            }
            previousKeys = [.. source.Keys];
        }
    }

    [Test]
    public void Rebuild_WithoutClear_DropsAllPreviousKeys()
    {
        using SortedMergeDictionary<int, int> dict = new();

        Dictionary<int, int> big = [];
        for (int i = 0; i < 300; i++) big[i] = i;
        dict.BuildFromUnsorted(big, Cmp);

        // Rebuild smaller without NoResizeClear: the new content replaces the old completely.
        Dictionary<int, int> small = [];
        for (int i = 1000; i < 1003; i++) small[i] = i;
        dict.BuildFromUnsorted(small, Cmp);

        Assert.That(dict.Count, Is.EqualTo(3));
        foreach (KeyValuePair<int, int> kv in small)
        {
            Assert.That(dict.TryGetValue(kv.Key, out int value), Is.True, $"missing key {kv.Key}");
            Assert.That(value, Is.EqualTo(kv.Value));
        }
        foreach (int stale in big.Keys)
        {
            Assert.That(dict.TryGetValue(stale, out _), Is.False, $"stale key {stale} resurrected");
        }
    }

    [Test]
    public void ThrowingKeep_MidMerge_LeavesDictionaryEmptyAndUsable()
    {
        using SortedMergeDictionary<int, int> dict = new();

        Dictionary<int, int> initial = [];
        for (int i = 0; i < 100; i++) initial[i] = i;
        dict.BuildFromUnsorted(initial, Cmp);

        // A keep delegate that throws mid-merge must not leave a lookup-able mix of old and new entries.
        using SortedMergeDictionary<int, int> source = FromPairs((200, 1), (201, 1), (202, 1), (203, 1));
        Assert.That(
            () => dict.BuildFromMerge([source], Cmp, static (_, key) => key < 202 ? true : throw new InvalidOperationException()),
            Throws.InvalidOperationException);

        Assert.That(dict.Count, Is.EqualTo(0));
        for (int i = 0; i < 100; i++) Assert.That(dict.TryGetValue(i, out _), Is.False, $"stale key {i} after failed merge");
        for (int i = 200; i < 204; i++) Assert.That(dict.TryGetValue(i, out _), Is.False, $"partial key {i} after failed merge");

        dict.BuildFromMerge([source], Cmp);
        Assert.That(dict.Count, Is.EqualTo(4));
        Assert.That(dict.TryGetValue(202, out _), Is.True);
    }

    [Test]
    public void ThrowingComparer_DuringSort_LeavesDictionaryEmptyAndReusable()
    {
        using SortedMergeDictionary<int, int> dict = FromPairs((1, 10), (2, 20), (3, 30));
        List<KeyValuePair<int, int>> source = [];
        for (int i = 20; i > 0; i--) source.Add(new KeyValuePair<int, int>(100 + i, i));

        Assert.That(
            () => dict.BuildFromUnsorted(source, new ThrowingComparer(3)),
            Throws.InvalidOperationException);

        AssertEmptyAndReusable(dict, 1, 2, 3, 101, 110, 120);
    }

    [Test]
    public void ThrowingSourceEnumerator_LeavesDictionaryEmptyAndReusable()
    {
        using SortedMergeDictionary<int, int> dict = FromPairs((1, 10), (2, 20), (3, 30));

        Assert.That(
            () => dict.BuildFromUnsorted(new ThrowingSource(5, 2), Cmp),
            Throws.InvalidOperationException);

        AssertEmptyAndReusable(dict, 1, 2, 3, 101, 102);
    }

    [Test]
    public void BucketSaltOverflow_ResetsSequenceAndPreservesLookups()
    {
        using SortedMergeDictionary<int, int> dict = new();
        Dictionary<int, int> initial = [];
        for (int i = 0; i < 64; i++) initial[i] = i * 10;
        dict.BuildFromUnsorted(initial, Cmp);

        BucketSaltField.SetValue(dict, int.MaxValue - 1);
        Dictionary<int, int> replacement = new()
        {
            [100] = 1,
            [200] = 2,
            [300] = 3,
            [400] = 4,
        };
        dict.BuildFromUnsorted(replacement, Cmp);

        Assert.That(BucketSaltField.GetValue(dict), Is.EqualTo(replacement.Count));
        foreach (KeyValuePair<int, int> kv in replacement)
        {
            Assert.That(dict.TryGetValue(kv.Key, out int value), Is.True, $"missing key {kv.Key}");
            Assert.That(value, Is.EqualTo(kv.Value), $"key {kv.Key}");
        }
        foreach (int staleKey in initial.Keys)
        {
            Assert.That(dict.TryGetValue(staleKey, out _), Is.False, $"stale key {staleKey} survived the reset");
        }
    }

    [TestCase(1)]
    [TestCase(5)]
    [TestCase(11)]
    [TestCase(22)]
    [TestCase(89)]
    [TestCase(1000)]
    public void BucketSize_MatchesLegacyLoadFactorRounding(int count)
    {
        using SortedMergeDictionary<int, int> dict = new();
        Dictionary<int, int> source = new(count);
        for (int i = 0; i < count; i++) source[i] = i;

        dict.BuildFromUnsorted(source, Cmp);

        uint expectedSize = BitOperations.RoundUpToPowerOf2((uint)(count / 0.7) + 1);
        Assert.That(BucketMaskField.GetValue(dict), Is.EqualTo(expectedSize - 1));
    }

    [TestCase(200, true)]
    [TestCase(10, false)]
    public void FilteredMerge_TightensDirtyMarkWithoutWeakeningAbortCleanup(int initialCount, bool clearBeforeMerge)
    {
        using SortedMergeDictionary<string, string> target = new();
        target.BuildFromUnsorted(CreateStringSource("old", initialCount), StringComparer.Ordinal);
        if (clearBeforeMerge) target.NoResizeClear();
        using SortedMergeDictionary<string, string> source =
            SortedMergeDictionary<string, string>.FromUnsorted(CreateStringSource("new", 100), StringComparer.Ordinal);

        target.BuildFromMerge([source], StringComparer.Ordinal, static (_, key) => string.CompareOrdinal(key, "new003") < 0);
        Assert.That(StringEntriesDirtyField.GetValue(target), Is.EqualTo(3));

        Assert.That(
            () => target.BuildFromMerge(
                [source],
                StringComparer.Ordinal,
                static (_, key) => key != "new020" ? true : throw new InvalidOperationException()),
            Throws.InvalidOperationException);
        Assert.That(StringEntriesDirtyField.GetValue(target), Is.EqualTo(100));

        target.NoResizeClear();
        SortedMergeDictionary<string, string>.Entry[] entries =
            (SortedMergeDictionary<string, string>.Entry[])StringEntriesField.GetValue(target)!;
        Assert.That(entries.All(static entry => entry.Key is null && entry.Value is null), Is.True);
    }

    [TestCase(40, 60)]
    [TestCase(60, 40)]
    public void CountEnumerationMismatch_ThrowsAndLeavesDictionaryEmptyAndReusable(int reportedCount, int actualCount)
    {
        using SortedMergeDictionary<int, int> dict = new();
        dict.BuildFromUnsorted(new MismatchedCountSource(3, 3), Cmp);

        Assert.That(
            () => dict.BuildFromUnsorted(new MismatchedCountSource(reportedCount, actualCount), Cmp),
            Throws.InvalidOperationException);

        Assert.That(dict.Count, Is.EqualTo(0));
        for (int i = 1; i <= Math.Max(reportedCount, actualCount); i++)
        {
            Assert.That(dict.TryGetValue(i * 3, out _), Is.False, $"key {i * 3} survived the failed build");
        }

        dict.BuildFromUnsorted(new MismatchedCountSource(2, 2), Cmp);
        Assert.That(dict.Count, Is.EqualTo(2));
        for (int i = 1; i <= 2; i++)
        {
            Assert.That(dict.TryGetValue(i * 3, out int value), Is.True, $"missing key {i * 3} after recovery");
            Assert.That(value, Is.EqualTo(i));
        }
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(37)]
    [TestCase(400)]
    public void SingleBucketChain_WalksItsFullLength(int count)
    {
        // Multiples of 65536 share the low bits, so every key lands in one bucket and the first-written key
        // sits at the very end of the chain - the walk must reach it, and a same-bucket miss must terminate.
        Dictionary<int, int> source = [];
        for (int i = 0; i < count; i++) source[i * 65536] = i;

        using SortedMergeDictionary<int, int> dict = SortedMergeDictionary<int, int>.FromUnsorted(source, Cmp);

        for (int i = 0; i < count; i++)
        {
            Assert.That(dict.TryGetValue(i * 65536, out int value), Is.True, $"missing chain entry {i}");
            Assert.That(value, Is.EqualTo(i));
        }
        Assert.That(dict.TryGetValue(500 * 65536, out _), Is.False);
    }

    private sealed class MismatchedCountSource(int reportedCount, int actualCount) : IReadOnlyCollection<KeyValuePair<int, int>>
    {
        public int Count => reportedCount;

        public IEnumerator<KeyValuePair<int, int>> GetEnumerator()
        {
            for (int i = 1; i <= actualCount; i++) yield return new KeyValuePair<int, int>(i * 3, i);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingComparer(int comparisonsBeforeThrow) : IComparer<int>
    {
        private int _remaining = comparisonsBeforeThrow;

        public int Compare(int x, int y)
        {
            if (_remaining-- == 0) throw new InvalidOperationException();
            return x.CompareTo(y);
        }
    }

    private sealed class ThrowingSource(int count, int itemsBeforeThrow) : IReadOnlyCollection<KeyValuePair<int, int>>
    {
        public int Count => count;

        public IEnumerator<KeyValuePair<int, int>> GetEnumerator()
        {
            for (int i = 1; i <= itemsBeforeThrow; i++) yield return new KeyValuePair<int, int>(100 + i, i);
            throw new InvalidOperationException();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static SortedMergeDictionary<int, int> FromPairs(params (int Key, int Value)[] pairs)
    {
        Dictionary<int, int> source = [];
        foreach ((int key, int value) in pairs) source[key] = value;
        return SortedMergeDictionary<int, int>.FromUnsorted(source, Cmp);
    }

    private static Dictionary<string, string> CreateStringSource(string prefix, int count)
    {
        Dictionary<string, string> source = new(count);
        for (int i = 0; i < count; i++) source[$"{prefix}{i:D3}"] = i.ToString();
        return source;
    }

    private static void AssertValue(SortedMergeDictionary<int, int> dict, int key, int expected)
    {
        Assert.That(dict.TryGetValue(key, out int value), Is.True, $"missing key {key}");
        Assert.That(value, Is.EqualTo(expected), $"key {key}");
    }

    private static void AssertEmptyAndReusable(SortedMergeDictionary<int, int> dict, params int[] absentKeys)
    {
        Assert.That(dict.Count, Is.EqualTo(0));
        foreach (int key in absentKeys)
        {
            Assert.That(dict.TryGetValue(key, out _), Is.False, $"key {key} survived the failed build");
        }

        dict.BuildFromUnsorted(new Dictionary<int, int> { [901] = 902 }, Cmp);
        Assert.That(dict.Count, Is.EqualTo(1));
        AssertValue(dict, 901, 902);
    }
}
