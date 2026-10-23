// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

/// <summary>
/// Bench (B'): <see cref="PatriciaTree.BulkSetAndHashByTopNibble"/> against <see cref="PatriciaTree.BulkSet"/> followed
/// by <see cref="PatriciaTree.UpdateRootHash"/>, with values short enough to embed nodes in their parents.
/// </summary>
public class FusedBulkSetTests
{
    [SetUp]
    public void RequireFusablePlatform()
    {
        if (!Avx2.IsSupported || Core.Cpu.RuntimeInformation.IsSingleProcessor) Assert.Ignore("The fused pass needs AVX2 and more than one core.");
    }

    [Test]
    public void Randomized_entries_match_bulk_set_then_hash([Range(0, 9)] int chunk)
    {
        for (int seed = chunk * 100; seed < (chunk + 1) * 100; seed++)
        {
            Random rng = new(seed);
            List<(ValueHash256 Key, byte[] Value)> pre = RandomEntries(rng, rng.Next(0, 2001), []);
            List<(ValueHash256 Key, byte[]? Value)> batch = RandomBatch(rng, pre, rng.Next(0, 2001));
            AssertSameAsBulkSetThenHash(pre, batch, $"seed {seed} (pre {pre.Count}, batch {batch.Count})");
        }
    }

    [Test]
    public void A_dirty_root_cannot_take_the_fused_pass()
    {
        Random rng = new(3);
        List<(ValueHash256 Key, byte[] Value)> pre = RandomEntries(rng, 500, []);
        PatriciaTree tree = new(new RawScopedTrieStore(BuildPreBlock(pre, out Hash256 root)), root, true, NullLogManager.Instance);
        Assert.That(tree.CanFuseBulkSetAndHash, Is.True, "sealed root");

        tree.Set(pre[0].Key.Bytes, [1, 2, 3]);
        int[] starts = new int[17];
        Assert.That(tree.CanFuseBulkSetAndHash, Is.False, "dirty root");
        Assert.Throws<InvalidOperationException>(() => tree.BulkSetAndHashByTopNibble([], starts, null));
    }

    private static void AssertSameAsBulkSetThenHash(List<(ValueHash256 Key, byte[] Value)> pre, List<(ValueHash256 Key, byte[]? Value)> batch, string label)
    {
        MemDb preDb = BuildPreBlock(pre, out Hash256 preRoot);
        MemDb dbOff = Copy(preDb);
        MemDb dbOn = Copy(preDb);
        PatriciaTree off = new(new RawScopedTrieStore(dbOff), preRoot, true, NullLogManager.Instance);
        PatriciaTree on = new(new RawScopedTrieStore(dbOn), preRoot, true, NullLogManager.Instance);

        using (ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(batch.Count))
        {
            foreach ((ValueHash256 key, byte[]? value) in batch) entries.Add(new PatriciaTree.BulkSetEntry(key, value));
            off.BulkSet(entries);
        }

        int[] starts = new int[17];
        PatriciaTree.BulkSetEntry[] grouped = GroupByTopNibble(batch, starts);
        on.BulkSetAndHashByTopNibble(grouped, starts, null);

        off.UpdateRootHash(canBeParallel: true);
        on.UpdateRootHash(canBeParallel: true);
        Assert.That(on.RootHash, Is.EqualTo(off.RootHash), $"{label}: root");

        off.Commit();
        on.Commit();
        HashSet<string> nodesOff = Contents(dbOff);
        HashSet<string> nodesOn = Contents(dbOn);
        if (!nodesOn.SetEquals(nodesOff))
        {
            Assert.Fail($"{label}: committed nodes differ, {nodesOn.Except(nodesOff).Count()} only fused, {nodesOff.Except(nodesOn).Count()} only bulk set");
        }
    }

    private static PatriciaTree.BulkSetEntry[] GroupByTopNibble(List<(ValueHash256 Key, byte[]? Value)> batch, int[] starts)
    {
        PatriciaTree.BulkSetEntry[] grouped = new PatriciaTree.BulkSetEntry[batch.Count];
        foreach ((ValueHash256 key, _) in batch) starts[(key.Bytes[0] >> 4) + 1]++;
        for (int nib = 0; nib < 16; nib++) starts[nib + 1] += starts[nib];
        int[] next = starts[..16];
        foreach ((ValueHash256 key, byte[]? value) in batch) grouped[next[key.Bytes[0] >> 4]++] = new PatriciaTree.BulkSetEntry(key, value);
        return grouped;
    }

    private static MemDb BuildPreBlock(List<(ValueHash256 Key, byte[] Value)> pre, out Hash256 root)
    {
        MemDb db = new();
        PatriciaTree tree = new(new RawScopedTrieStore(db), NullLogManager.Instance);
        using (ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(pre.Count))
        {
            foreach ((ValueHash256 key, byte[] value) in pre) entries.Add(new PatriciaTree.BulkSetEntry(key, value));
            tree.BulkSet(entries);
        }

        tree.UpdateRootHash(canBeParallel: true);
        tree.Commit();
        root = tree.RootHash;
        return db;
    }

    private static MemDb Copy(MemDb db)
    {
        MemDb copy = new();
        foreach (KeyValuePair<byte[], byte[]> entry in db.GetAll()) copy[entry.Key] = entry.Value;
        return copy;
    }

    private static HashSet<string> Contents(MemDb db) =>
        [.. db.GetAll().Select(static entry => $"{Convert.ToHexString(entry.Key)}:{Convert.ToHexString(entry.Value)}")];

    private static byte[] RandomValue(Random rng)
    {
        byte[] value = new byte[rng.Next(4) == 0 ? rng.Next(33, 80) : rng.Next(1, 5)];
        rng.NextBytes(value);
        return value;
    }

    /// <summary>Random keys, some in clusters that share all but their last two bytes, so the short values there sit
    /// in nodes embedded in their parents.</summary>
    private static List<(ValueHash256 Key, byte[] Value)> RandomEntries(Random rng, int count, HashSet<ValueHash256> taken)
    {
        List<byte[]> clusters = [];
        for (int i = rng.Next(0, 6); i > 0; i--)
        {
            byte[] prefix = new byte[32];
            rng.NextBytes(prefix);
            clusters.Add(prefix);
        }

        List<(ValueHash256 Key, byte[] Value)> entries = [];
        while (entries.Count < count)
        {
            byte[] key = new byte[32];
            rng.NextBytes(key);
            if (clusters.Count > 0 && rng.Next(3) == 0)
            {
                byte[] prefix = clusters[rng.Next(clusters.Count)];
                prefix.AsSpan(0, 30).CopyTo(key);
                key[30] &= 0x0F;
            }

            ValueHash256 path = new(key);
            if (taken.Add(path)) entries.Add((path, RandomValue(rng)));
        }

        return entries;
    }

    private static List<(ValueHash256 Key, byte[]? Value)> RandomBatch(Random rng, List<(ValueHash256 Key, byte[] Value)> pre, int count)
    {
        HashSet<ValueHash256> taken = [.. pre.Select(static entry => entry.Key)];
        HashSet<ValueHash256> inBatch = [];
        List<(ValueHash256 Key, byte[]? Value)> batch = [];
        foreach ((ValueHash256 key, byte[] value) in RandomEntries(rng, count / 2, taken))
        {
            // New keys: created, or deleted while absent.
            inBatch.Add(key);
            batch.Add((key, rng.Next(10) == 0 ? null : value));
        }

        while (batch.Count < count && pre.Count > inBatch.Count)
        {
            (ValueHash256 key, byte[] value) = pre[rng.Next(pre.Count)];
            if (!inBatch.Add(key)) continue;
            int op = rng.Next(10);
            batch.Add((key, op < 3 ? null : op < 4 ? value : RandomValue(rng)));
        }

        return batch;
    }
}
