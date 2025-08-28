// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.HighPerformance;
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
        yield return new TestCaseData(GenRandomOfLength(10)).SetName("10");
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


        yield return new TestCaseData(GenRandomOfLength(100), GenRandomOfLength(100)).SetName("reapply");
        for (int i = 0; i < 10; i++)
        {
            yield return new TestCaseData(GenRandomOfLength(1000, i), GenRandomOfLength(1000, i*2)).SetName($"random {i}");
        }

        List<(Hash256 key, byte[] value)> list = GenRandomOfLength(100);
        List<(Hash256 key, byte[] value)> eraseList = list.Select<(Hash256 key, byte[] value), (Hash256 key, byte[] value)>((k) => (k.key, null)).ToList();

        yield return new TestCaseData(GenRandomOfLength(100), eraseList).SetName("delete");

    }

    static byte[] MakeRandomValue(Random rng)
    {
        if (rng.NextDouble() < 0.05)
        {
            return null;
        }
        if (rng.NextDouble() < 0.05)
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
        Hash256 root;
        const bool recordDump = true;
        String originalDump = "";

        TimeSpan baselineTime;
        {
            IScopedTrieStore trieStore = new RawScopedTrieStore(new TestMemDb());
            PatriciaTree pTree = new PatriciaTree(trieStore, LimboLogs.Instance);
            pTree.RootHash = Keccak.EmptyTreeHash;

            foreach (var existingItem in existingItems)
            {
                pTree.Set(existingItem.key.Bytes, existingItem.value);
            }
            pTree.Commit();

            long sw = Stopwatch.GetTimestamp();

            foreach (var valueTuple in items) pTree.Set(valueTuple.key.Bytes, valueTuple.value);

            pTree.UpdateRootHash();

            baselineTime = Stopwatch.GetElapsedTime(sw);

            if (recordDump)
            {
                pTree.Commit();
                TreeDumper td = new TreeDumper();
                pTree.Accept(td, pTree.RootHash);
                originalDump = td.ToString();
            }
            root = pTree.RootHash;
        }

        TimeSpan newTime;
        {
            IScopedTrieStore trieStore = new RawScopedTrieStore(new TestMemDb());
            PatriciaTree pTree = new PatriciaTree(trieStore, LimboLogs.Instance);
            pTree.RootHash = Keccak.EmptyTreeHash;

            foreach (var existingItem in existingItems)
            {
                pTree.Set(existingItem.key.Bytes, existingItem.value);
            }

            pTree.Commit();

            long sw = Stopwatch.GetTimestamp();

            using ArrayPoolList<PatriciaTreeBulkSetter.BulkSetEntry> entries = new ArrayPoolList<PatriciaTreeBulkSetter.BulkSetEntry>(items.Count);
            foreach (var valueTuple in items)
            {
                entries.Add(new PatriciaTreeBulkSetter.BulkSetEntry(valueTuple.key, valueTuple.value));
            }

            PatriciaTreeBulkSetter.BulkSetUnsorted(pTree, entries.AsMemory());
            pTree.UpdateRootHash();
            newTime = Stopwatch.GetElapsedTime(sw);

            if (recordDump)
            {
                TreeDumper td = new TreeDumper();
                pTree.Commit();
                pTree.Accept(td, pTree.RootHash);
                if (pTree.RootHash != root)
                {
                    TestContext.Error.WriteLine($"Baseline {originalDump}");
                    TestContext.Error.WriteLine($"But got {td.ToString()}");
                }
            }

            pTree.RootHash.Should().Be(root);
        }
        TestContext.Error.WriteLine($"Time is {newTime} vs {baselineTime}");
    }
#pragma warning restore CS0162 // Unreachable code detected

    public static IEnumerable<TestCaseData> HexarySearchTestCases()
    {
        yield return new TestCaseData(
            new List<Hash256>()
            {
                new Hash256("1111111111111111111111111111111111111111111111111111111111111111"),
                new Hash256("1111111111111111111111111111111111111111111111111111111111111111"),
                new Hash256("1111111111111111111111111111111111111111111111111111111111111111"),
                new Hash256("4111111111111111111111111111111111111111111111111111111111111111"),
                new Hash256("4111111111111111111111111111111111111111111111111111111111111111"),
                new Hash256("4111111111111111111111111111111111111111111111111111111111111111"),
            },
            new (int, int)[]
            {
                ( 1, 0 ),
                ( 4, 3 )
            }).SetName("base");
    }

    [TestCaseSource(nameof(HexarySearchTestCases))]
    public void HexarySearch(List<Hash256> paths, (int, int)[] expectedResult)
    {
        List<PatriciaTreeBulkSetter.BulkSetEntry> items = new List<PatriciaTreeBulkSetter.BulkSetEntry>(paths.Count);
        foreach (Hash256 hash256 in paths)
        {
            items.Add(new PatriciaTreeBulkSetter.BulkSetEntry(hash256, Array.Empty<byte>()));
        }

        Span<(int, int)> result = stackalloc (int, int)[TrieNode.BranchesCount];
        int resultNum = PatriciaTreeBulkSetter.HexarySearch(items.AsSpan(), 0, result);

        (int,int)[] asArray = new (int, int)[resultNum];
        for (int i = 0; i < resultNum; i++)
        {
            asArray[i] = (result[i].Item1, result[i].Item2);
        }

        asArray.Should().BeEquivalentTo(expectedResult);
    }
}
