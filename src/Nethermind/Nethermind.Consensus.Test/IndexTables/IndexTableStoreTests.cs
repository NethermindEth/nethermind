// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.IndexTables;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.IndexTables;

public class IndexTableStoreTests
{
    [Test]
    public void Store_and_retrieve_roundtrip()
    {
        IndexTableStore store = new();
        List<IndexEntry> entries = [IndexEntry.CreateBlock(TestItem.KeccakA, 0)];

        store.Store(0, 0, entries);

        IReadOnlyList<IndexEntry> retrieved = store.Get(0, 0);
        Assert.That(retrieved, Is.Not.Null);
        Assert.That(retrieved.Count, Is.EqualTo(1));
    }

    [Test]
    public void Get_nonexistent_returns_null()
    {
        IndexTableStore store = new();
        Assert.That(store.Get(0, 999), Is.Null);
    }

    [Test]
    public void Remove_deletes_entry()
    {
        IndexTableStore store = new();
        List<IndexEntry> entries = [IndexEntry.CreateBlock(TestItem.KeccakA, 0)];

        store.Store(0, 0, entries);
        store.Remove(0, 0);

        Assert.That(store.Get(0, 0), Is.Null);
    }

    [Test]
    public void InvalidateAbove_removes_entries_above_threshold()
    {
        IndexTableStore store = new();

        store.Store(0, 5, [IndexEntry.CreateBlock(TestItem.KeccakA, 5)]);
        store.Store(0, 10, [IndexEntry.CreateBlock(TestItem.KeccakB, 10)]);
        store.Store(0, 15, [IndexEntry.CreateBlock(TestItem.KeccakC, 15)]);

        store.InvalidateAbove(10);

        Assert.Multiple(() =>
        {
            Assert.That(store.Get(0, 5), Is.Not.Null);
            Assert.That(store.Get(0, 10), Is.Not.Null);
            Assert.That(store.Get(0, 15), Is.Null);
        });
    }

    [Test]
    public void InvalidateAbove_works_across_levels()
    {
        IndexTableStore store = new();

        store.Store(0, 5, [IndexEntry.CreateBlock(TestItem.KeccakA, 5)]);
        store.Store(1, 8, [IndexEntry.CreateBlock(TestItem.KeccakB, 8)]);
        store.Store(2, 20, [IndexEntry.CreateBlock(TestItem.KeccakC, 20)]);

        store.InvalidateAbove(7);

        Assert.Multiple(() =>
        {
            Assert.That(store.Get(0, 5), Is.Not.Null);
            Assert.That(store.Get(1, 8), Is.Null);
            Assert.That(store.Get(2, 20), Is.Null);
        });
    }

    [Test]
    public void InvalidateAbove_keeps_multi_block_table_ending_at_the_retained_head()
    {
        IndexTableStore store = new();

        // Level-1 tables span 4 blocks: [4, 7] and [8, 11].
        store.Store(1, 4, [IndexEntry.CreateBlock(TestItem.KeccakA, 4)]);
        store.Store(1, 8, [IndexEntry.CreateBlock(TestItem.KeccakB, 8)]);

        store.InvalidateAbove(7);

        Assert.Multiple(() =>
        {
            Assert.That(store.Get(1, 4), Is.Not.Null);
            Assert.That(store.Get(1, 8), Is.Null);
        });
    }

    [TestCase(1, 4L, 5L)]
    [TestCase(2, 0L, 9L)]
    [TestCase(4, 256L, 300L)]
    public void InvalidateAbove_removes_table_starting_below_but_ending_above_threshold(int level, long firstBlock, long retainedHead)
    {
        IndexTableStore store = new();
        store.Store(level, firstBlock, [IndexEntry.CreateBlock(TestItem.KeccakA, (ulong)firstBlock)]);

        store.InvalidateAbove(retainedHead);

        Assert.That(store.Get(level, firstBlock), Is.Null);
    }

    [Test]
    public void Store_at_different_levels_are_independent()
    {
        IndexTableStore store = new();

        store.Store(0, 0, [IndexEntry.CreateBlock(TestItem.KeccakA, 0)]);
        store.Store(1, 0, [IndexEntry.CreateBlock(TestItem.KeccakB, 0)]);

        IReadOnlyList<IndexEntry> level0 = store.Get(0, 0);
        IReadOnlyList<IndexEntry> level1 = store.Get(1, 0);

        Assert.Multiple(() =>
        {
            Assert.That(level0, Is.Not.Null);
            Assert.That(level1, Is.Not.Null);
            // They should be different entries
            Assert.That(level0, Is.Not.SameAs(level1));
        });
    }

    [Test]
    public void Store_with_block_hash_distinguishes_competing_forks()
    {
        IndexTableStore store = new();
        List<IndexEntry> branchA = [IndexEntry.CreateBlock(TestItem.KeccakA, 100)];
        List<IndexEntry> branchB = [IndexEntry.CreateBlock(TestItem.KeccakB, 100)];

        store.Store(0, 100, branchA, TestItem.KeccakC);
        store.Store(0, 100, branchB, TestItem.KeccakD);

        Assert.Multiple(() =>
        {
            IReadOnlyList<IndexEntry> retrievedA = store.Get(0, 100, TestItem.KeccakC);
            IReadOnlyList<IndexEntry> retrievedB = store.Get(0, 100, TestItem.KeccakD);
            IReadOnlyList<IndexEntry> latest = store.Get(0, 100);

            Assert.That(retrievedA, Is.Not.Null);
            Assert.That(retrievedA[0].CompareTo(branchA[0]), Is.EqualTo(0));

            Assert.That(retrievedB, Is.Not.Null);
            Assert.That(retrievedB[0].CompareTo(branchB[0]), Is.EqualTo(0));

            Assert.That(latest, Is.Not.Null);
            Assert.That(latest[0].CompareTo(branchB[0]), Is.EqualTo(0));
        });
    }
}
