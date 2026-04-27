// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.State.Flat.BlockRangeTrieForest;
using Nethermind.Trie;
using NUnit.Framework;
using ForestImpl = Nethermind.State.Flat.BlockRangeTrieForest.BlockRangeTrieForest;

namespace Nethermind.State.Flat.Test.BlockRangeTrieForest;

[TestFixture]
public class BlockRangeForestDeletionDriverTests
{
    private static SnapshotableMemDb CreateDb() => new(nameof(BlockRangeForestDeletionDriverTests));

    private static ForestImpl CreateForest(SnapshotableMemDb db) => new(db);

    private static void PopulateStateNodes(IBlockRangeTrieForest forest, long blockRange, int count)
    {
        using IBlockRangeTrieForest.IWriter writer = forest.CreateWriter();
        for (int i = 0; i < count; i++)
        {
            byte[] hashBytes = new byte[32];
            hashBytes[0] = (byte)i;
            hashBytes[1] = (byte)blockRange;
            ValueHash256 hash = new(hashBytes);
            TreePath path = new(hash, i % 64);
            writer.PutState(blockRange, path, hash, [(byte)(0xC0 + i)]);
        }
        writer.Flush();
    }

    [Test]
    public void DeleteBatch_DeletesUpToCount_WithinRange()
    {
        using SnapshotableMemDb db = CreateDb();
        ForestImpl forest = CreateForest(db);
        using MemColumnsDb<FlatDbColumns> metaDb = new();

        // Populate range 0 with 10 nodes, range 1 with 10 nodes
        PopulateStateNodes(forest, 0, 10);
        PopulateStateNodes(forest, 1, 10);

        BlockRangeForestDeletionDriver driver = new(forest, metaDb);

        // Delete up to 5 from below range 1 (i.e., range 0)
        driver.DeleteBatch(belowBlockRange: 1, count: 5);

        // After deleting 5 from range 0, range 1 keys should still be present
        int remainingRange0 = CountKeysInRange(db, 0);
        int remainingRange1 = CountKeysInRange(db, 1);
        Assert.That(remainingRange0, Is.EqualTo(5));
        Assert.That(remainingRange1, Is.EqualTo(10));
    }

    [Test]
    public void DeleteBatch_DrainsBelowRange_AcrossMultipleBatches()
    {
        using SnapshotableMemDb db = CreateDb();
        ForestImpl forest = CreateForest(db);
        using MemColumnsDb<FlatDbColumns> metaDb = new();

        PopulateStateNodes(forest, 0, 6);

        BlockRangeForestDeletionDriver driver = new(forest, metaDb);

        driver.DeleteBatch(belowBlockRange: 1, count: 4);
        Assert.That(CountKeysInRange(db, 0), Is.EqualTo(2));

        driver.DeleteBatch(belowBlockRange: 1, count: 4);
        Assert.That(CountKeysInRange(db, 0), Is.EqualTo(0));
    }

    [Test]
    public void DeleteBatch_CursorPersists_AcrossDriverInstances()
    {
        using SnapshotableMemDb db = CreateDb();
        ForestImpl forest = CreateForest(db);
        using MemColumnsDb<FlatDbColumns> metaDb = new();

        PopulateStateNodes(forest, 0, 10);

        // First driver deletes 6
        BlockRangeForestDeletionDriver driver1 = new(forest, metaDb);
        driver1.DeleteBatch(belowBlockRange: 1, count: 6);
        Assert.That(CountKeysInRange(db, 0), Is.EqualTo(4));

        // Second driver picks up from cursor
        BlockRangeForestDeletionDriver driver2 = new(forest, metaDb);
        driver2.DeleteBatch(belowBlockRange: 1, count: 10);
        Assert.That(CountKeysInRange(db, 0), Is.EqualTo(0));
    }

    [Test]
    public void DeleteBatch_DoesNothing_WhenBelowBlockRangeIsZero()
    {
        using SnapshotableMemDb db = CreateDb();
        ForestImpl forest = CreateForest(db);
        using MemColumnsDb<FlatDbColumns> metaDb = new();

        PopulateStateNodes(forest, 0, 5);

        BlockRangeForestDeletionDriver driver = new(forest, metaDb);
        driver.DeleteBatch(belowBlockRange: 0, count: 100);

        Assert.That(CountKeysInRange(db, 0), Is.EqualTo(5));
    }

    private static int CountKeysInRange(SnapshotableMemDb db, long blockRange)
    {
        Span<byte> lower = stackalloc byte[4];
        BlockRangeForestKey.EncodeRangePrefix(lower, blockRange);
        byte[] upper = BlockRangeForestKey.RangeUpperBoundKey(blockRange);
        int count = 0;
        using ISortedView view = db.GetViewBetween(lower, upper);
        while (view.MoveNext()) count++;
        return count;
    }
}
