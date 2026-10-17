// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public partial class WarmUpPathForDeleteTests
{
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

    private sealed class CancellationState
    {
        public bool Cancelled;
    }
}
