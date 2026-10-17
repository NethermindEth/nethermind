// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public partial class WarmUpPathForDeleteTests
{
    [TestCase(8, TestName = "WarmUpPaths_EightCachedPaths_AvoidsBatchResolution")]
    [TestCase(64, TestName = "WarmUpPaths_FullCachedChunk_AvoidsBatchResolution")]
    [TestCase(65, TestName = "WarmUpPaths_CachedTail_AvoidsBatchResolution")]
    [TestCase(512, TestName = "WarmUpPaths_ManyCachedChunks_AvoidsBatchResolution")]
    public void WarmUpPaths_WhenPathsAreCached_DoesNotInvokeBatchResolver(int count)
    {
        using CountingMemDb db = new();
        PatriciaTree original = new(new RawScopedTrieStore(db), NullLogManager.Instance);
        PatriciaTree.WarmUpEntry[] entries = new PatriciaTree.WarmUpEntry[count];
        for (int i = 0; i < count; i++)
        {
            byte[] key = new byte[32];
            BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(28), i + 1);
            original.Set(key, Value);
            entries[i] = new(new ValueHash256(key));
        }
        original.Commit();
        PathCachingTrieStore store = new(db);
        PatriciaTree tree = new(store, NullLogManager.Instance) { RootHash = original.RootHash };
        foreach (PatriciaTree.WarmUpEntry entry in entries) tree.WarmUpPath(entry.Key.Bytes);
        int reads = db.Reads;
        Assert.That(reads, Is.GreaterThan(0), "precondition: scalar warmup must populate the cache");
        tree.WarmUpPaths(entries);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.BatchSizes, Is.Empty, "cached paths must not request RLP batches");
            Assert.That(db.Reads, Is.EqualTo(reads), "cached warmup must not read persistence");
            Assert.That(tree.RootHash, Is.EqualTo(original.RootHash), "cached traversal must preserve the root");
        }
    }

    [TestCase(false, TestName = "WarmUpPaths_CachedDeleteWithColdSibling_LoadsSibling")]
    [TestCase(true, TestName = "WarmUpPaths_CachedDeleteWithWarmSibling_AvoidsBatchResolution")]
    public void WarmUpPaths_WhenDeletePathIsCached_PreservesSiblingWarmup(bool siblingCached)
    {
        (CountingMemDb ownedDb, Hash256 root) = Commit(KeyA, KeyB, KeyC);
        using CountingMemDb db = ownedDb;
        PathCachingTrieStore store = new(db);
        PatriciaTree tree = new(store, NullLogManager.Instance) { RootHash = root };
        tree.WarmUpPath(KeyA, forDelete: siblingCached);
        PatriciaTree.WarmUpEntry[] entries = Enumerable.Repeat(new PatriciaTree.WarmUpEntry(new ValueHash256(KeyA), true), 8).ToArray();
        int beforeWarmup = db.Reads;
        tree.WarmUpPaths(entries);
        int beforeDelete = db.Reads;
        tree.Set(KeyA, []);
        tree.UpdateRootHash();
        (CountingMemDb ownedExpected, Hash256 expected) = Commit(KeyB, KeyC);
        using CountingMemDb expectedDb = ownedExpected;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(beforeDelete - beforeWarmup, Is.EqualTo(siblingCached ? 0 : 1), "only a cold collapse sibling may require a read");
            Assert.That(db.Reads, Is.EqualTo(beforeDelete), "delete commit must find its collapse sibling warm");
            Assert.That(tree.RootHash, Is.EqualTo(expected), "the cached path must preserve deletion behavior");
            if (siblingCached) Assert.That(store.BatchSizes, Is.Empty, "a cached delete and sibling need no batch scratch");
        }
    }

    [TestCase(8, TestName = "WarmUpPaths_CachedPrefixWithBatchTail_WarmsRemainingPaths")]
    [TestCase(9, TestName = "WarmUpPaths_CachedPrefixWithScalarTail_WarmsRemainingPaths")]
    public void WarmUpPaths_WhenOnlyPrefixIsCached_WarmsEveryRemainingPath(int cachedCount)
    {
        (CountingMemDb ownedDb, Hash256 root) = Commit(KeyA, KeyB, KeyC);
        using CountingMemDb db = ownedDb;
        PathCachingTrieStore store = new(db);
        PatriciaTree tree = new(store, NullLogManager.Instance) { RootHash = root };
        tree.WarmUpPath(KeyA);
        PatriciaTree.WarmUpEntry[] entries = Enumerable.Range(0, 16)
            .Select(i => new PatriciaTree.WarmUpEntry(new ValueHash256(i < cachedCount ? KeyA : (i % 2 == 0 ? KeyB : KeyC))))
            .ToArray();
        tree.WarmUpPaths(entries);
        int reads = db.Reads;
        Assert.That(tree.Get(KeyB).ToArray(), Is.EqualTo(Value), "the first unresolved tail must be warmed");
        Assert.That(tree.Get(KeyC).ToArray(), Is.EqualTo(Value), "all paths after the first miss must be warmed");
        Assert.That(db.Reads, Is.EqualTo(reads), "both batch and scalar tails must leave their paths cached");
    }

    [Test]
    public void WarmUpPaths_WhenCancelledDuringCachedPrefix_DoesNotReadColdTail()
    {
        (CountingMemDb ownedDb, Hash256 root) = Commit(KeyA, KeyB, KeyC);
        using CountingMemDb db = ownedDb;
        PathCachingTrieStore store = new(db);
        PatriciaTree tree = new(store, NullLogManager.Instance) { RootHash = root };
        tree.WarmUpPath(KeyA);
        PatriciaTree.WarmUpEntry[] entries = Enumerable.Range(0, 16)
            .Select(i => new PatriciaTree.WarmUpEntry(new ValueHash256(i == 0 ? KeyA : KeyC))).ToArray();
        int reads = db.Reads;
        int checks = 0;
        tree.WarmUpPaths(entries, false, _ => ++checks == 2);
        Assert.That(checks, Is.EqualTo(2), "cancellation must be checked after the first cached path");
        Assert.That(db.Reads, Is.EqualTo(reads), "cancellation must leave the cold tail unread");
        tree.Get(KeyC);
        Assert.That(db.Reads, Is.GreaterThan(reads), "precondition: the tail was actually cold before cancellation");
    }

    [TestCase(8, false, TestName = "WarmUpPaths_SharedPrefix_ReadsIndependentLeavesTogether")]
    [TestCase(8, true, TestName = "WarmUpPaths_EmbeddedLeaves_DoNotLoadThemFromPersistence")]
    [TestCase(64, false, TestName = "WarmUpPaths_FullChunk_PreservesUpdateRoot")]
    [TestCase(65, false, TestName = "WarmUpPaths_TailChunk_PreservesUpdateRoot")]
    [TestCase(257, false, TestName = "WarmUpPaths_MultipleChunks_KeepFrontiersBounded")]
    public void WarmUpPaths_BeforeUpdates_PreservesRootAndAvoidsFurtherReads(int count, bool embedded)
    {
        using CountingMemDb db = new();
        PatriciaTree original = new(new RawScopedTrieStore(db), NullLogManager.Instance);
        byte[][] keys = new byte[count][];
        PatriciaTree.WarmUpEntry[] entries = new PatriciaTree.WarmUpEntry[count];
        for (int i = 0; i < count; i++)
        {
            keys[i] = new byte[32];
            BinaryPrimitives.WriteUInt16BigEndian(keys[i].AsSpan(30), (ushort)(i + 1));
            original.Set(keys[i], embedded ? [1] : Value);
            entries[count - i - 1] = new(new ValueHash256(keys[i]));
        }
        original.Commit();
        PathCachingTrieStore store = new(db);
        PatriciaTree tree = new(store, NullLogManager.Instance) { RootHash = original.RootHash };
        tree.WarmUpPaths(entries);
        int readsBeforeUpdate = db.Reads;
        using MemDb expectedDb = new();
        PatriciaTree expected = new(new RawScopedTrieStore(expectedDb), NullLogManager.Instance);
        foreach (byte[] key in keys)
        {
            tree.Set(key, [0xBB, 0xCC]);
            expected.Set(key, [0xBB, 0xCC]);
        }
        tree.UpdateRootHash();
        expected.UpdateRootHash();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.RootHash, Is.EqualTo(expected.RootHash), "frontier warmup must preserve the resulting root");
            Assert.That(db.Reads - readsBeforeUpdate, Is.Zero, "all nodes required by these updates must already be warm");
            Assert.That(store.BatchSizes, Has.All.LessThanOrEqualTo(64), "each frontier must remain within its bounded chunk");
            Assert.That(store.BatchSizes.Any(size => size > 1), Is.EqualTo(!embedded), "hashed leaves must be read together, while embedded leaves need no persistence request");
        }
    }

    [Test]
    public void WarmUpPaths_WithDuplicateDeleteFlags_WarmsTheCollapseSibling()
    {
        (CountingMemDb ownedDb, Hash256 root) = Commit(KeyA, KeyB, KeyC);
        using CountingMemDb db = ownedDb;
        PatriciaTree tree = OpenCold(db, root);
        PatriciaTree.WarmUpEntry[] entries = Enumerable.Range(0, 8)
            .Select(i => new PatriciaTree.WarmUpEntry(new ValueHash256(KeyA), forDelete: i == 7)).ToArray();
        tree.WarmUpPaths(entries);
        int reads = db.Reads;
        tree.Set(KeyA, []);
        tree.UpdateRootHash();
        (CountingMemDb ownedExpected, Hash256 expected) = Commit(KeyB, KeyC);
        using CountingMemDb expectedDb = ownedExpected;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.Reads - reads, Is.Zero, "a delete flag on any duplicate must warm the collapsing branch's sibling");
            Assert.That(tree.RootHash, Is.EqualTo(expected), "duplicate merging must preserve delete behavior");
        }
    }

    [Test]
    public void WarmUpPaths_WhenCancelledAfterRead_DoesNotPublishLoadedRlp()
    {
        (CountingMemDb ownedDb, Hash256 root) = Commit(KeyA, KeyB, KeyC);
        using CountingMemDb db = ownedDb;
        CancellationState state = new();
        PathCachingTrieStore store = new(db) { AfterBatchRead = () => state.Cancelled = true };
        PatriciaTree tree = new(store, NullLogManager.Instance) { RootHash = root };
        PatriciaTree.WarmUpEntry[] entries = Enumerable.Repeat(new PatriciaTree.WarmUpEntry(new ValueHash256(KeyA)), 8).ToArray();
        Assert.That(tree.RootRef!.HasRlp, Is.False, "precondition: the cold root has no loaded RLP");
        tree.WarmUpPaths(entries, state, static cancellation => cancellation.Cancelled);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.BatchSizes, Has.Count.EqualTo(1), "the deterministic hook must cancel after the first read");
            Assert.That(tree.RootRef.HasRlp, Is.False, "a cancelled batch must not publish its loaded bytes");
            Assert.That(tree.RootHash, Is.EqualTo(root), "cancellation must not change the root");
        }
    }

    [Test]
    public void WarmUpPaths_WithOneMissingSubtree_StillWarmsOtherPaths()
    {
        using CountingMemDb db = new();
        PatriciaTree original = new(new RawScopedTrieStore(db), NullLogManager.Instance);
        byte[][] keys = new byte[8][];
        PatriciaTree.WarmUpEntry[] entries = new PatriciaTree.WarmUpEntry[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = new byte[32];
            keys[i][0] = (byte)((i + 1) << 4);
            original.Set(keys[i], Value);
            entries[i] = new(new ValueHash256(keys[i]));
        }
        original.Commit();
        PathCachingTrieStore store = new(db) { MissingNibble = 1 };
        PatriciaTree tree = new(store, NullLogManager.Instance) { RootHash = original.RootHash };
        tree.WarmUpPaths(entries);
        int reads = db.Reads;
        for (int i = 1; i < keys.Length; i++) Assert.That(tree.Get(keys[i]).ToArray(), Is.EqualTo(Value), "a missing sibling must not prevent warming a valid path");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.Reads, Is.EqualTo(reads), "valid paths must remain cached after another subtree was missing");
            Assert.That(tree.RootHash, Is.EqualTo(original.RootHash), "a missing subtree during warmup must not change the root");
        }
    }

    [Test]
    public void WarmUpTargets_WithIndependentTries_PreservesEveryRoot([Values(8, 16, 32)] int targetCount, [Values] bool delete, [Values] bool pathCached)
    {
        using CountingMemDb db = new();
        PatriciaTree.WarmUpTarget[] targets = new PatriciaTree.WarmUpTarget[targetCount];
        PatriciaTree.WarmUpWork[] work = new PatriciaTree.WarmUpWork[targetCount * 2];
        PathCachingTrieStore[] stores = new PathCachingTrieStore[targetCount];
        for (int i = 0; i < targetCount; i++)
        {
            PatriciaTree original = new(new RawScopedTrieStore(db), NullLogManager.Instance);
            byte[] value = new byte[64];
            value[^1] = (byte)(i + 1);
            foreach (byte[] key in new[] { KeyA, KeyB, KeyC }) original.Set(key, value);
            original.Commit();
            stores[i] = new(db);
            PatriciaTree tree = new(stores[i], NullLogManager.Instance) { RootHash = original.RootHash };
            if (pathCached) tree.WarmUpPath(KeyA);
            targets[i] = new(tree, tree.RootRef);
            work[i] = new(i, new(new ValueHash256(KeyA)));
            work[work.Length - i - 1] = new(i, new(new ValueHash256(KeyA), delete));
        }
        List<int[]> batches = [];
        PatriciaTree.WarmUpTargets(targets, work, new MixedTargetReader(stores, batches), false, static _ => false);
        int reads = db.Reads;
        for (int i = 0; i < targetCount; i++)
        {
            byte[] value = new byte[64];
            value[^1] = (byte)(i + 1);
            using MemDb expectedDb = new();
            PatriciaTree expected = new(new RawScopedTrieStore(expectedDb), NullLogManager.Instance);
            expected.Set(KeyB, value);
            expected.Set(KeyC, value);
            if (!delete) expected.Set(KeyA, [0xdd, (byte)i]);
            expected.UpdateRootHash();
            targets[i].Tree.Set(KeyA, delete ? [] : [0xdd, (byte)i]);
            targets[i].Tree.UpdateRootHash();
            Assert.That(targets[i].Tree.RootHash, Is.EqualTo(expected.RootHash), "target identity and duplicate delete flags must remain independent");
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.Reads, Is.EqualTo(reads), "every required path and delete sibling must be warmed");
            if (pathCached && !delete)
                Assert.That(batches, Is.Empty, "fully cached targets need no persistence read");
            else
            {
                Assert.That(batches, Is.Not.Empty);
                Assert.That(batches[0].Distinct().Count(), Is.EqualTo(targetCount), "independent roots or cold delete siblings must reach the same frontier read");
            }
            Assert.That(batches, Has.All.Length.LessThanOrEqualTo(64));
        }
    }

    [Test]
    public void WarmUpTargets_WhenOneChildLookupFails_StillWarmsOtherTargets()
    {
        const int count = 8;
        using CountingMemDb db = new();
        PatriciaTree.WarmUpTarget[] targets = new PatriciaTree.WarmUpTarget[count];
        PatriciaTree.WarmUpWork[] work = new PatriciaTree.WarmUpWork[count];
        PathCachingTrieStore[] stores = new PathCachingTrieStore[count];
        for (int i = 0; i < count; i++)
        {
            PatriciaTree original = new(new RawScopedTrieStore(db), NullLogManager.Instance);
            byte[] value = new byte[64];
            value[^1] = (byte)(i + 1);
            foreach (byte[] key in new[] { KeyA, KeyB, KeyC }) original.Set(key, value);
            original.Commit();
            stores[i] = new(db);
            PatriciaTree tree = new(stores[i], NullLogManager.Instance) { RootHash = original.RootHash };
            targets[i] = new(tree, tree.RootRef);
            work[i] = new(i, new(new ValueHash256(KeyA)));
        }
        stores[0].ThrowChildLookup = true;
        List<int[]> batches = [];
        PatriciaTree.WarmUpTargets(targets, work, new MixedTargetReader(stores, batches), false, static _ => false);
        int reads = db.Reads;
        for (int i = 1; i < count; i++)
        {
            byte[] actual = targets[i].Tree.Get(KeyA).ToArray();
            Assert.That(actual[^1], Is.EqualTo((byte)(i + 1)));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.Reads, Is.EqualTo(reads), "a cache/child-lookup failure must not discard other targets");
            Assert.That(batches[0].Distinct().Count(), Is.EqualTo(count));
        }
    }

    private sealed class StaleWarmupException : Exception;

    private readonly struct MixedTargetReader(PathCachingTrieStore[] stores, List<int[]> batches) : PatriciaTree.IWarmUpReader
    {
        public void LoadRlps(scoped ReadOnlySpan<(TreePath Path, Hash256 Hash)> requests, scoped ReadOnlySpan<int> targets,
            scoped Span<byte[]?> values)
        {
            batches.Add(targets.ToArray());
            for (int i = 0; i < requests.Length; i++)
                values[i] = stores[targets[i]].TryLoadRlp(in requests[i].Path, requests[i].Hash);
        }
        public bool IsUnavailable(Exception exception) => exception is StaleWarmupException;
    }

    private sealed class CancellationState
    {
        public bool Cancelled;
    }
}
