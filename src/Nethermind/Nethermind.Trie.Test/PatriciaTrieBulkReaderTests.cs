// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public class PatriciaTrieBulkReaderTests
{
    private struct CollectingSink : IPatriciaTrieBulkReaderSink<CollectingSink>
    {
        public byte[][] Results;

        public CollectingSink(int count)
        {
            Results = new byte[count][];
        }

        public void OnRead(in ValueHash256 key, int idx, ReadOnlySpan<byte> value)
        {
            Results[idx] = value.IsEmpty ? [] : value.ToArray();
        }
    }

    [Test]
    public void EmptyTree_ReturnsEmptyForAllKeys()
    {
        IScopedTrieStore trieStore = new RawScopedTrieStore(new TestMemDb());
        PatriciaTree tree = new(trieStore, LimboLogs.Instance);

        ValueHash256[] keys =
        [
            new ValueHash256("1111111111111111111111111111111111111111111111111111111111111111"),
            new ValueHash256("2222222222222222222222222222222222222222222222222222222222222222"),
        ];

        CollectingSink sink = new(keys.Length);
        PatriciaTrieBulkReader.BulkRead(trieStore, tree.RootRef, keys, ref sink);

        for (int i = 0; i < keys.Length; i++)
        {
            sink.Results[i].Should().BeEmpty();
        }
    }

    [Test]
    public void SingleEntry_MatchesIndividualGet()
    {
        IScopedTrieStore trieStore = new RawScopedTrieStore(new TestMemDb());
        PatriciaTree tree = new(trieStore, LimboLogs.Instance);

        Hash256 key = new("abcdef0000000000000000000000000000000000000000000000000000000000");
        byte[] value = [1, 2, 3, 4, 5];
        tree.Set(key.Bytes, value);
        tree.Commit();

        ValueHash256[] keys = [(ValueHash256)key];
        CollectingSink sink = new(1);
        PatriciaTrieBulkReader.BulkRead(trieStore, tree.RootRef, keys, ref sink);

        ReadOnlySpan<byte> expected = tree.Get(key.Bytes);
        sink.Results[0].Should().BeEquivalentTo(expected.ToArray());
    }

    [TestCase(10, 0)]
    [TestCase(100, 1)]
    [TestCase(1000, 2)]
    public void MultipleEntries_MatchesIndividualGet(int count, int seed)
    {
        IScopedTrieStore trieStore = new RawScopedTrieStore(new TestMemDb());
        PatriciaTree tree = new(trieStore, LimboLogs.Instance);

        Random rng = new(seed);
        List<(Hash256 key, byte[] value)> items = GenRandomEntries(count, rng);

        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(items.Count);
        foreach ((Hash256 key, byte[] value) in items)
        {
            entries.Add(new PatriciaTree.BulkSetEntry(key, value));
        }

        tree.BulkSet(entries);
        tree.Commit();

        ValueHash256[] keys = new ValueHash256[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            keys[i] = (ValueHash256)items[i].key;
        }

        CollectingSink sink = new(keys.Length);
        PatriciaTrieBulkReader.BulkRead(trieStore, tree.RootRef, keys, ref sink);

        for (int i = 0; i < keys.Length; i++)
        {
            ReadOnlySpan<byte> expected = tree.Get(items[i].key.Bytes);
            byte[] expectedArray = expected.IsEmpty ? [] : expected.ToArray();
            sink.Results[i].Should().BeEquivalentTo(expectedArray, $"mismatch at index {i}, key {items[i].key}");
        }
    }

    [Test]
    public void MissingKeys_ReturnsEmpty()
    {
        IScopedTrieStore trieStore = new RawScopedTrieStore(new TestMemDb());
        PatriciaTree tree = new(trieStore, LimboLogs.Instance);

        // Populate with some entries
        tree.Set(new Hash256("aaa0000000000000000000000000000000000000000000000000000000000000").Bytes, [1, 2, 3]);
        tree.Set(new Hash256("bbb0000000000000000000000000000000000000000000000000000000000000").Bytes, [4, 5, 6]);
        tree.Commit();

        // Read keys that don't exist
        ValueHash256[] keys =
        [
            new ValueHash256("ccc0000000000000000000000000000000000000000000000000000000000000"),
            new ValueHash256("ddd0000000000000000000000000000000000000000000000000000000000000"),
        ];

        CollectingSink sink = new(keys.Length);
        PatriciaTrieBulkReader.BulkRead(trieStore, tree.RootRef, keys, ref sink);

        for (int i = 0; i < keys.Length; i++)
        {
            sink.Results[i].Should().BeEmpty();
        }
    }

    [Test]
    public void MixOfExistingAndMissing()
    {
        IScopedTrieStore trieStore = new RawScopedTrieStore(new TestMemDb());
        PatriciaTree tree = new(trieStore, LimboLogs.Instance);

        Hash256 existingKey = new("aaaa000000000000000000000000000000000000000000000000000000000000");
        byte[] existingValue = [10, 20, 30];
        tree.Set(existingKey.Bytes, existingValue);
        tree.Commit();

        ValueHash256[] keys =
        [
            (ValueHash256)existingKey,
            new ValueHash256("bbbb000000000000000000000000000000000000000000000000000000000000"),
        ];

        CollectingSink sink = new(keys.Length);
        PatriciaTrieBulkReader.BulkRead(trieStore, tree.RootRef, keys, ref sink);

        ReadOnlySpan<byte> expected = tree.Get(existingKey.Bytes);
        sink.Results[0].Should().BeEquivalentTo(expected.ToArray());
        sink.Results[1].Should().BeEmpty();
    }

    [Test]
    public void TopLevelBranch_MatchesIndividualGet()
    {
        IScopedTrieStore trieStore = new RawScopedTrieStore(new TestMemDb());
        PatriciaTree tree = new(trieStore, LimboLogs.Instance);

        Hash256[] insertKeys = new Hash256[16];
        byte[][] values = new byte[16][];
        for (int i = 0; i < 16; i++)
        {
            byte[] keyBytes = new byte[32];
            keyBytes[0] = (byte)(i << 4); // Top nibble is i
            insertKeys[i] = new Hash256(keyBytes);
            values[i] = [(byte)i, (byte)(i + 1)];
            tree.Set(insertKeys[i].Bytes, values[i]);
        }
        tree.Commit();

        ValueHash256[] readKeys = new ValueHash256[16];
        for (int i = 0; i < 16; i++)
        {
            readKeys[i] = (ValueHash256)insertKeys[i];
        }

        CollectingSink sink = new(readKeys.Length);
        PatriciaTrieBulkReader.BulkRead(trieStore, tree.RootRef, readKeys, ref sink);

        for (int i = 0; i < readKeys.Length; i++)
        {
            ReadOnlySpan<byte> expected = tree.Get(insertKeys[i].Bytes);
            sink.Results[i].Should().BeEquivalentTo(expected.ToArray(), $"mismatch at nibble {i}");
        }
    }

    [Test]
    public void DeepExtension_MatchesIndividualGet()
    {
        IScopedTrieStore trieStore = new RawScopedTrieStore(new TestMemDb());
        PatriciaTree tree = new(trieStore, LimboLogs.Instance);

        Hash256 key1 = new("3333333333333333333333333333333333333333333333333333333333333333");
        Hash256 key2 = new("3333333332222222222222222222222222222222222222222222222222222222");
        tree.Set(key1.Bytes, [1, 2, 3]);
        tree.Set(key2.Bytes, [4, 5, 6]);
        tree.Commit();

        ValueHash256[] readKeys = [(ValueHash256)key1, (ValueHash256)key2];
        CollectingSink sink = new(2);
        PatriciaTrieBulkReader.BulkRead(trieStore, tree.RootRef, readKeys, ref sink);

        sink.Results[0].Should().BeEquivalentTo(tree.Get(key1.Bytes).ToArray());
        sink.Results[1].Should().BeEquivalentTo(tree.Get(key2.Bytes).ToArray());
    }

    [Test]
    public void EmptyKeys_DoesNothing()
    {
        IScopedTrieStore trieStore = new RawScopedTrieStore(new TestMemDb());
        PatriciaTree tree = new(trieStore, LimboLogs.Instance);

        CollectingSink sink = new(0);
        PatriciaTrieBulkReader.BulkRead(trieStore, tree.RootRef, ReadOnlySpan<ValueHash256>.Empty, ref sink);
        // No exception is success
    }

    [Test]
    public void OriginalIndex_IsPreserved()
    {
        IScopedTrieStore trieStore = new RawScopedTrieStore(new TestMemDb());
        PatriciaTree tree = new(trieStore, LimboLogs.Instance);

        // Insert in order f, e, d, ... 0 (reverse) to ensure sorting doesn't corrupt index mapping
        Hash256[] insertKeys = new Hash256[16];
        for (int i = 0; i < 16; i++)
        {
            byte[] keyBytes = new byte[32];
            keyBytes[0] = (byte)(i << 4);
            insertKeys[i] = new Hash256(keyBytes);
            tree.Set(insertKeys[i].Bytes, [(byte)(i * 10)]);
        }
        tree.Commit();

        // Query in reverse order
        ValueHash256[] readKeys = new ValueHash256[16];
        for (int i = 0; i < 16; i++)
        {
            readKeys[i] = (ValueHash256)insertKeys[15 - i];
        }

        CollectingSink sink = new(16);
        PatriciaTrieBulkReader.BulkRead(trieStore, tree.RootRef, readKeys, ref sink);

        for (int i = 0; i < 16; i++)
        {
            ReadOnlySpan<byte> expected = tree.Get(insertKeys[15 - i].Bytes);
            sink.Results[i].Should().BeEquivalentTo(expected.ToArray(), $"index {i} should map to key {15 - i}");
        }
    }

    [TestCase(16)]
    [TestCase(64)]
    [TestCase(256)]
    [TestCase(1024)]
    public void BulkRead_vs_OneByOne_vs_ParallelFor(int keyCount)
    {
        const int treeSize = 4096;
        const int warmup = 5;
        const int iterations = 100;

        TestMemDb db = new();
        IScopedTrieStore trieStore = new RawScopedTrieStore(db);
        PatriciaTree tree = new(trieStore, LimboLogs.Instance);

        Random rng = new(42);
        ValueHash256[] allKeys = new ValueHash256[treeSize];

        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(treeSize);
        for (int i = 0; i < treeSize; i++)
        {
            byte[] keyBuffer = new byte[32];
            rng.NextBytes(keyBuffer);
            allKeys[i] = new ValueHash256(keyBuffer);
            byte[] valueBuffer = new byte[32];
            rng.NextBytes(valueBuffer);
            entries.Add(new PatriciaTree.BulkSetEntry(in allKeys[i], valueBuffer));
        }
        tree.BulkSet(entries);
        tree.UpdateRootHash();
        Hash256 rootHash = tree.RootHash;
        tree.Commit();

        ValueHash256[] readKeys = new ValueHash256[keyCount];
        Random readRng = new(123);
        for (int i = 0; i < keyCount; i++)
        {
            readKeys[i] = allKeys[readRng.Next(treeSize)];
        }

        // Warmup all paths (fresh root each time)
        for (int w = 0; w < warmup; w++)
        {
            PatriciaTree t = new(trieStore, rootHash, true, LimboLogs.Instance);
            for (int i = 0; i < readKeys.Length; i++) t.Get(readKeys[i].Bytes);
            TrieNode root = trieStore.FindCachedOrUnknown(TreePath.Empty, rootHash);
            NoOpSink sink = default;
            PatriciaTrieBulkReader.BulkRead(trieStore, root, readKeys, ref sink);
            PatriciaTree t2 = new(trieStore, rootHash, true, LimboLogs.Instance);
            Parallel.For(0, readKeys.Length, (i) => t2.Get(readKeys[i].Bytes));
        }

        // Measure one-by-one (fresh root per iteration)
        long sw1 = Stopwatch.GetTimestamp();
        for (int iter = 0; iter < iterations; iter++)
        {
            PatriciaTree t = new(trieStore, rootHash, true, LimboLogs.Instance);
            for (int i = 0; i < readKeys.Length; i++) t.Get(readKeys[i].Bytes);
        }
        TimeSpan oneByOneTime = Stopwatch.GetElapsedTime(sw1);

        // Measure Parallel.For on individual reads (fresh root per iteration)
        long sw2 = Stopwatch.GetTimestamp();
        for (int iter = 0; iter < iterations; iter++)
        {
            PatriciaTree t = new(trieStore, rootHash, true, LimboLogs.Instance);
            Parallel.For(0, readKeys.Length, (i) => t.Get(readKeys[i].Bytes));
        }
        TimeSpan parallelForTime = Stopwatch.GetElapsedTime(sw2);

        // Measure BulkRead (fresh root per iteration)
        long sw3 = Stopwatch.GetTimestamp();
        for (int iter = 0; iter < iterations; iter++)
        {
            TrieNode root = trieStore.FindCachedOrUnknown(TreePath.Empty, rootHash);
            NoOpSink sink = default;
            PatriciaTrieBulkReader.BulkRead(trieStore, root, readKeys, ref sink);
        }
        TimeSpan bulkTime = Stopwatch.GetElapsedTime(sw3);

        double oneByOneUs = oneByOneTime.TotalMicroseconds / iterations;
        double parallelForUs = parallelForTime.TotalMicroseconds / iterations;
        double bulkUs = bulkTime.TotalMicroseconds / iterations;

        string msg = $"Keys={keyCount} | OneByOne={oneByOneUs:F1}us | Parallel.For={parallelForUs:F1}us | BulkRead={bulkUs:F1}us | Bulk/OneByOne={oneByOneUs / bulkUs:F2}x | Bulk/Parallel={parallelForUs / bulkUs:F2}x";
        Assert.Warn(msg);
    }

    private struct NoOpSink : IPatriciaTrieBulkReaderSink<NoOpSink>
    {
        public void OnRead(in ValueHash256 key, int idx, ReadOnlySpan<byte> value) { }
    }

    private static List<(Hash256 key, byte[] value)> GenRandomEntries(int count, Random rng)
    {
        List<(Hash256 key, byte[] value)> items = new(count);
        for (int i = 0; i < count; i++)
        {
            byte[] keyBuffer = new byte[32];
            rng.NextBytes(keyBuffer);
            byte[] valueBuffer = new byte[32];
            rng.NextBytes(valueBuffer);
            items.Add((new Hash256(keyBuffer), valueBuffer));
        }
        return items;
    }
}
