// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test;

public class PatriciaTreeBulkSetterTests
{
    public static IEnumerable<TestCaseData> NewBranchesGen()
    {
        Random rng = new(0);

        yield return new TestCaseData(GenRandomOfLength(1)).SetName("1");
        yield return new TestCaseData(GenRandomOfLength(1)).SetName("2");

        for (int i = 0; i < 10; i++)
        {
            yield return new TestCaseData(GenRandomOfLength(10, seed: i)).SetName($"10 {i}");
        }
        yield return new TestCaseData(GenRandomOfLength(100)).SetName("100");
        yield return new TestCaseData(GenRandomOfLength(1000)).SetName("1000");

        yield return new TestCaseData(new List<(Hash256 key, byte[] value)>()
        {
            (new Hash256("0000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("1000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("2000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("3000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("4000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("5000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("6000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("7000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("8000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("9000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("a000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("b000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("c000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("d000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("e000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("f000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
        }).SetName("top level branch");

        yield return new TestCaseData(new List<(Hash256 key, byte[] value)>()
        {
            (new Hash256("a000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("a100000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("a200000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("a300000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("a400000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("a500000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("a600000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("a700000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("a800000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("a900000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("aa00000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("ab00000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("ac00000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("ad00000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("ae00000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("af00000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
        }).SetName("second level branch");

        yield return new TestCaseData(new List<(Hash256 key, byte[] value)>()
        {
            (new Hash256("a000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("a100000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("f000000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("f100000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("f200000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
        }).SetName("multi last hex");

        yield return new TestCaseData(new List<(Hash256 key, byte[] value)>()
        {
            (new Hash256("aaaa000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("aaaadddd00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("bbbbdddd00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("bbbbeeee00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("cccccccc00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            (new Hash256("cccc000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
        }).SetName("deep value");
    }

    public static IEnumerable<TestCaseData> PreExistingDataGen()
    {
        Random rng = new(0);

        yield return new TestCaseData(new List<(Hash256 key, byte[] value)>()).SetName("baseline");

        yield return new TestCaseData(new List<(Hash256 key, byte[] value)>()
        {
            (new Hash256("3333333333333333333333333333333333333333333333333333333333333333"), MakeRandomValue(rng)),
        }).SetName("one long leaf");

        yield return new TestCaseData(new List<(Hash256 key, byte[] value)>()
        {
            (new Hash256("3333333333333333333333333333333333333333333333333333333333333333"), MakeRandomValue(rng)),
            (new Hash256("3322222222222222222222222222222222222222222222222222222222222222"), MakeRandomValue(rng)),
        }).SetName("one extension");

        yield return new TestCaseData(GenRandomOfLength(1000)).SetName("random 1000");
    }

    public static IEnumerable<TestCaseData> BulkSetTestGen()
    {
        Random rng = new(0);

        foreach (TestCaseData existingData in PreExistingDataGen())
        {
            foreach (TestCaseData testCaseData in NewBranchesGen())
            {
                yield return new TestCaseData(existingData.Arguments[0], testCaseData.Arguments[0]).SetName(existingData.TestName + " and " + testCaseData.TestName);
            }
        }

        yield return new TestCaseData(
            new List<(Hash256 key, byte[] value)>()
            {
                (new Hash256("aaaa000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("aaaadddd00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
            },
            new List<(Hash256 key, byte[] value)>()
            {
                (new Hash256("aaaadddd00000000000000000000000000000000000000000000000000000000"), null),
            }
        ).SetName("simple delete");

        yield return new TestCaseData(
            new List<(Hash256 key, byte[] value)>()
            {
                (new Hash256("aaaa000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("aaaadddd00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("bbbbdddd00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("bbbbeeee00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("cccccccc00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("cccc000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng))
            },
            new List<(Hash256 key, byte[] value)>()
            {
                (new Hash256("aaaadddd00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("bbbbeeee00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("cccc000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng))
            }
        ).SetName("replace");

        yield return new TestCaseData(
            new List<(Hash256 key, byte[] value)>()
            {
                (new Hash256("aaaa000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("aaaadddd00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("bbbbdddd00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("bbbbeeee00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("cccccccc00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("cccc000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng))
            },
            new List<(Hash256 key, byte[] value)>()
            {
                (new Hash256("aaaadddd00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("bbbbeeee00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng)),
                (new Hash256("cccc000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng))
            }
        ).SetName("replace");

        var reappliedEntry =
            new List<(Hash256 key, byte[] value)>()
            {
                (new Hash256("abaa000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng, canBeNull: false)),
                (new Hash256("aaaadddd00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng, canBeNull: false)),
                (new Hash256("bbbbdddd00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng, canBeNull: false)),
                (new Hash256("bbbbeeee00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng, canBeNull: false)),
                (new Hash256("cccccccc00000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng, canBeNull: false)),
                (new Hash256("cccc000000000000000000000000000000000000000000000000000000000000"), MakeRandomValue(rng, canBeNull: false))
            };
        yield return new TestCaseData(reappliedEntry, reappliedEntry).SetName("reapply specific");

        yield return new TestCaseData(GenRandomOfLength(100), GenRandomOfLength(100)).SetName("reapply 100");
        for (int i = 0; i < 10; i++)
        {
            yield return new TestCaseData(GenRandomOfLength(1000, i), GenRandomOfLength(1000, i*2)).SetName($"random {i}");
        }

        List<(Hash256 key, byte[] value)> list = GenRandomOfLength(100);
        List<(Hash256 key, byte[] value)> eraseList = list.Select<(Hash256 key, byte[] value), (Hash256 key, byte[] value)>((k) => (k.key, null)).ToList();

        yield return new TestCaseData(GenRandomOfLength(100), eraseList).SetName("delete");

    }

    static byte[] MakeRandomValue(Random rng, bool canBeNull = true)
    {
        if (canBeNull && rng.NextDouble() < 0.05)
        {
            return null;
        }
        if (canBeNull && rng.NextDouble() < 0.05)
        {
            return [];
        }
        byte[] randData = new byte[32];
        rng.NextBytes(randData);
        return randData;
    }

    private static List<(Hash256 key, byte[] value)> GenRandomOfLength(int itemCount, int seed = 0)
    {
        Random rng = new Random(seed);
        List<(Hash256 key, byte[] value)> items = new List<(Hash256 key, byte[] value)>(0);

        for (int i = 0; i < itemCount; i++)
        {
            byte[] buffer = new byte[32];
            rng.NextBytes(buffer);
            Hash256 key = new Hash256(buffer);
            rng.NextBytes(buffer);

            items.Add((key,  buffer.AsSpan().ToArray()));
        }

        return items;
    }

#pragma warning disable CS0162 // Unreachable code detected
    [TestCaseSource(nameof(BulkSetTestGen))]
    public void BulkSet(List<(Hash256 key, byte[] value)> existingItems, List<(Hash256 key, byte[] value)> items)
    {
        const bool recordDump = true;
        (Hash256 root, TimeSpan baselineTime, long baselineWriteCount, string originalDump) = CalculateBaseline(existingItems, items, recordDump);

        TimeSpan newTime;
        long newWriteCount = 0;
        {
            TestMemDb db = new TestMemDb();
            IScopedTrieStore trieStore = new RawScopedTrieStore(db);
            PatriciaTree pTree = new PatriciaTree(trieStore, LimboLogs.Instance);
            pTree.RootHash = Keccak.EmptyTreeHash;

            foreach (var existingItem in existingItems)
            {
                pTree.Set(existingItem.key.Bytes, existingItem.value);
            }

            pTree.Commit();

            using ArrayPoolList<PatriciaTree.BulkSetEntry> entries = new ArrayPoolList<PatriciaTree.BulkSetEntry>(items.Count);
            foreach (var valueTuple in items)
            {
                entries.Add(new PatriciaTree.BulkSetEntry(valueTuple.key, valueTuple.value));
            }

            long sw = Stopwatch.GetTimestamp();
            pTree.BulkSet(entries);
            pTree.Commit();
            newTime = Stopwatch.GetElapsedTime(sw);
            newWriteCount = db.WritesCount;

            if (recordDump)
            {
                TreeDumper td = new TreeDumper(expectAccounts: false);
                pTree.Commit();
                pTree.Accept(td, pTree.RootHash);
                if (pTree.RootHash != root)
                {
                    TestContext.Error.WriteLine($"Baseline {originalDump}");
                    TestContext.Error.WriteLine($"But got {td.ToString()}");
                }
            }

            newWriteCount.Should().BeLessOrEqualTo(baselineWriteCount);
            pTree.RootHash.Should().Be(root);
        }

        TestContext.Error.WriteLine($"Time is Baseline: {baselineTime}, Bulk: {newTime}");
        TestContext.Error.WriteLine($"Write count is Baseline: {baselineWriteCount}, Bulk: {newWriteCount}");
        newWriteCount.Should().BeLessOrEqualTo(baselineWriteCount);
    }

    [TestCaseSource(nameof(BulkSetTestGen))]
    public void BulkSetPreSorted(List<(Hash256 key, byte[] value)> existingItems, List<(Hash256 key, byte[] value)> items)
    {
        const bool recordDump = false;
        (Hash256 root, TimeSpan baselineTime, long baselineWriteCount, string originalDump) = CalculateBaseline(existingItems, items, recordDump);

        TimeSpan preSortedTime;
        long preSortedWriteCount;
        {
            TestMemDb db = new TestMemDb();
            IScopedTrieStore trieStore = new RawScopedTrieStore(db);
            PatriciaTree pTree = new PatriciaTree(trieStore, LimboLogs.Instance);
            pTree.RootHash = Keccak.EmptyTreeHash;

            foreach (var existingItem in existingItems)
            {
                pTree.Set(existingItem.key.Bytes, existingItem.value);
            }

            pTree.Commit();


            using ArrayPoolList<PatriciaTree.BulkSetEntry> entries = new ArrayPoolList<PatriciaTree.BulkSetEntry>(items.Count);
            foreach (var valueTuple in items)
            {
                entries.Add(new PatriciaTree.BulkSetEntry(valueTuple.key, valueTuple.value));
            }

            entries.AsSpan().Sort();

            long sw = Stopwatch.GetTimestamp();
            pTree.BulkSet(entries, PatriciaTree.Flags.WasSorted);
            pTree.Commit();
            preSortedWriteCount = db.WritesCount;
            preSortedTime = Stopwatch.GetElapsedTime(sw);

            if (recordDump)
            {
                TreeDumper td = new TreeDumper(expectAccounts: false);
                pTree.Commit();
                pTree.Accept(td, pTree.RootHash);
                if (pTree.RootHash != root)
                {
                    TestContext.Error.WriteLine($"Baseline {originalDump}");
                    TestContext.Error.WriteLine($"But in sorted got {td.ToString()}");
                }
            }

            pTree.RootHash.Should().Be(root);
        }

        TestContext.Error.WriteLine($"Time is Baseline: {baselineTime}, Sorted Bulk: {preSortedTime}");
        TestContext.Error.WriteLine($"Write count is Baseline: {baselineWriteCount}, Sorted Bulk: {preSortedWriteCount}");
        preSortedWriteCount.Should().BeLessOrEqualTo(baselineWriteCount);
    }

    [TestCaseSource(nameof(BulkSetTestGen))]
    public void BulkSetOneByOne(List<(Hash256 key, byte[] value)> existingItems, List<(Hash256 key, byte[] value)> items)
    {
        const bool recordDump = false;
        (Hash256 root, TimeSpan baselineTime, long baselineWriteCount, string originalDump) = CalculateBaseline(existingItems, items, recordDump);

        TimeSpan bulkSetOne;
        long writeCount;

        {
            // Just the bulk set one stack
            TestMemDb db = new TestMemDb();
            IScopedTrieStore trieStore = new RawScopedTrieStore(db);
            PatriciaTree pTree = new PatriciaTree(trieStore, LimboLogs.Instance);
            pTree.RootHash = Keccak.EmptyTreeHash;

            foreach (var existingItem in existingItems)
            {
                pTree.Set(existingItem.key.Bytes, existingItem.value);
            }

            pTree.Commit();


            long sw = Stopwatch.GetTimestamp();
            PatriciaTree.ThreadResource threadResource = new PatriciaTree.ThreadResource();
            foreach (var valueTuple in items)
            {
                using ArrayPoolList<PatriciaTree.BulkSetEntry> entries = new ArrayPoolList<PatriciaTree.BulkSetEntry>(items.Count);
                entries.Add(new PatriciaTree.BulkSetEntry(valueTuple.key, valueTuple.value));
                pTree.BulkSet(entries, PatriciaTree.Flags.None);
            }

            pTree.Commit();
            writeCount = db.WritesCount;
            bulkSetOne = Stopwatch.GetElapsedTime(sw);

            if (recordDump)
            {
                TreeDumper td = new TreeDumper(expectAccounts: false);
                pTree.Commit();
                pTree.Accept(td, pTree.RootHash);
                if (pTree.RootHash != root)
                {
                    TestContext.Error.WriteLine($"Baseline {originalDump}");
                    TestContext.Error.WriteLine($"But in multiple set one got {td.ToString()}");
                }
            }

            pTree.RootHash.Should().Be(root);
        }

        TestContext.Error.WriteLine($"Time is Baseline: {baselineTime}, One by one time: {bulkSetOne}");
        TestContext.Error.WriteLine($"Write count is Baseline: {baselineWriteCount}, Write count: {writeCount}");
        writeCount.Should().BeLessOrEqualTo(baselineWriteCount);
    }

    private static (Hash256, TimeSpan, long, string originalDump) CalculateBaseline(List<(Hash256 key, byte[] value)> existingItems, List<(Hash256 key, byte[] value)> items, bool recordDump)
    {
        Hash256 root;
        String originalDump = "";
        TimeSpan baselineTime;
        long baselineWriteCount = 0;
        {
            TestMemDb db = new TestMemDb();
            IScopedTrieStore trieStore = new RawScopedTrieStore(db);
            PatriciaTree pTree = new PatriciaTree(trieStore, LimboLogs.Instance);
            pTree.RootHash = Keccak.EmptyTreeHash;

            foreach (var existingItem in existingItems)
            {
                pTree.Set(existingItem.key.Bytes, existingItem.value);
            }
            pTree.Commit();

            long sw = Stopwatch.GetTimestamp();

            foreach (var valueTuple in items) pTree.Set(valueTuple.key.Bytes, valueTuple.value);

            pTree.Commit();

            baselineTime = Stopwatch.GetElapsedTime(sw);

            if (recordDump)
            {
                pTree.Commit();
                TreeDumper td = new TreeDumper(expectAccounts: false);
                pTree.Accept(td, pTree.RootHash);
                originalDump = td.ToString();
            }
            root = pTree.RootHash;

            baselineWriteCount = db.WritesCount;
        }
        return (root, baselineTime, baselineWriteCount, originalDump);
    }
#pragma warning restore CS0162 // Unreachable code detected

    [Test]
    public void BulkSet_ShouldThrowOnNonUniqueEntries()
    {
        IScopedTrieStore trieStore = new RawScopedTrieStore(new TestMemDb());
        PatriciaTree pTree = new PatriciaTree(trieStore, LimboLogs.Instance);
        pTree.RootHash = Keccak.EmptyTreeHash;

        Random rng = new Random(0);

        using ArrayPoolList<PatriciaTree.BulkSetEntry> entries = new ArrayPoolList<PatriciaTree.BulkSetEntry>(3);
        entries.Add(new PatriciaTree.BulkSetEntry(new ValueHash256("8818888888888888888888888888888888888888888888888888888888888888"), MakeRandomValue(rng)));
        entries.Add(new PatriciaTree.BulkSetEntry(new ValueHash256("8828888888888888888888888888888888888888888888888888888888888888"), MakeRandomValue(rng)));
        entries.Add(new PatriciaTree.BulkSetEntry(new ValueHash256("8848888888888888888888888888888888888888888888888888888888888888"), MakeRandomValue(rng)));
        entries.Add(new PatriciaTree.BulkSetEntry(new ValueHash256("8848888888888888888888888888888888888888888888888888888888888888"), MakeRandomValue(rng)));
        entries.Add(new PatriciaTree.BulkSetEntry(new ValueHash256("8858888888888888888888888888888888888888888888888888888888888888"), MakeRandomValue(rng)));

        var act = () => pTree.BulkSet(entries);
        act.Should().Throw<InvalidOperationException>();
    }

    public static IEnumerable<TestCaseData> BucketSortTestCase()
    {
        yield return new TestCaseData(
            new List<ValueHash256>()
            {
                new("1211111111111111111111111111111111111111111111111111111111111111"),
                new("4111111111111111111111111111111111111111111111111111111111111111"),
                new("1111111111111111111111111111111111111111111111111111111111111111"),
                new("4211111111111111111111111111111111111111111111111111111111111111"),
            },
            new List<ValueHash256>()
            {
                new("1211111111111111111111111111111111111111111111111111111111111111"),
                new("4111111111111111111111111111111111111111111111111111111111111111"),
                new("1111111111111111111111111111111111111111111111111111111111111111"),
                new("4211111111111111111111111111111111111111111111111111111111111111"),
            },
            new (int, int)[]
            {
                ( 1, 0 ),
                ( 4, 2 )
            }).SetName("base");
    }

    [TestCaseSource(nameof(BucketSortTestCase))]
    public void TestBucketSort(List<ValueHash256> paths, List<ValueHash256> expectedPaths, (int, int)[] expectedResult)
    {
        using ArrayPoolList<PatriciaTree.BulkSetEntry> items = new ArrayPoolList<PatriciaTree.BulkSetEntry>(paths.Count);
        foreach (ValueHash256 ValueHash256 in paths)
        {
            items.Add(new PatriciaTree.BulkSetEntry(ValueHash256, Array.Empty<byte>()));
        }

        Span<(int, int)> result = stackalloc (int, int)[TrieNode.BranchesCount];
        using ArrayPoolList<PatriciaTree.BulkSetEntry> buffer = new ArrayPoolList<PatriciaTree.BulkSetEntry>(paths.Count, paths.Count);
        using ArrayPoolList<byte> nibbleBuffer = new ArrayPoolList<byte>(paths.Count, paths.Count);
        int resultNum = PatriciaTree.BucketSort16(items.AsSpan(), buffer.AsSpan(), nibbleBuffer.AsSpan(), 0, result);

        List<ValueHash256> partiallySortedPaths = buffer.Select((it) => it.Path).ToList();
        partiallySortedPaths.Should().BeEquivalentTo(expectedPaths);

        (int,int)[] asArray = new (int, int)[resultNum];
        for (int i = 0; i < resultNum; i++)
        {
            asArray[i] = (result[i].Item1, result[i].Item2);
        }

        asArray.Should().BeEquivalentTo(expectedResult);
    }

    [TestCaseSource(nameof(BucketSortTestCase))]
    public void HexarySearch(List<ValueHash256> paths, List<ValueHash256> expectedPaths, (int, int)[] expectedResult)
    {
        using ArrayPoolList<PatriciaTree.BulkSetEntry> items = new ArrayPoolList<PatriciaTree.BulkSetEntry>(paths.Count);
        foreach (ValueHash256 hash256 in paths)
        {
            items.Add(new PatriciaTree.BulkSetEntry(hash256, Array.Empty<byte>()));
        }
        items.AsSpan().Sort();

        Span<(int, int)> result = stackalloc (int, int)[TrieNode.BranchesCount];
        int resultNum = PatriciaTree.HexarySearchAlreadySorted(items.AsSpan(), 0, result);

        (int,int)[] asArray = new (int, int)[resultNum];
        for (int i = 0; i < resultNum; i++)
        {
            asArray[i] = (result[i].Item1, result[i].Item2);
        }

        asArray.Should().BeEquivalentTo(expectedResult);
    }
}
