// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.StateDiffsWriter.Data;
using Nethermind.StateDiffsWriter.Storage;
using NUnit.Framework;

namespace Nethermind.StateDiffsWriter.Test.Storage;

[TestFixture]
public class BlockDiffsStoreTests
{
    private MemColumnsDb<BlockDiffsColumns> _db = null!;
    private BlockDiffsStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new MemColumnsDb<BlockDiffsColumns>();
        _store = new BlockDiffsStore(_db);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    [Test]
    public void WriteThenRead_RoundTripsRecord()
    {
        BlockDiffRecord record = new(
            BlockNumber: 100,
            StateRoot: TestItem.KeccakA,
            CodeHashChanges: [new CodeHashEntry(default, TestItem.KeccakB.ValueHash256, 42)],
            SlotCountChanges: [new SlotCountEntry(TestItem.KeccakC.ValueHash256, 0, 3)]);

        _store.WriteBlockDiff(record);

        BlockDiffRecord? read = _store.ReadBlockDiff(100);
        Assert.That(read, Is.Not.Null);
        Assert.That(read!.BlockNumber, Is.EqualTo(record.BlockNumber));
        Assert.That(read.StateRoot, Is.EqualTo(record.StateRoot));
        Assert.That(read.CodeHashChanges, Is.EqualTo(record.CodeHashChanges));
        Assert.That(read.SlotCountChanges, Is.EqualTo(record.SlotCountChanges));
    }

    [Test]
    public void WriteUpdatesSlotCountsCf()
    {
        BlockDiffRecord record = new(
            BlockNumber: 1,
            StateRoot: TestItem.KeccakA,
            CodeHashChanges: [],
            SlotCountChanges:
            [
                new SlotCountEntry(TestItem.KeccakB.ValueHash256, 0, 5),
                new SlotCountEntry(TestItem.KeccakC.ValueHash256, 10, 12),
            ]);

        _store.WriteBlockDiff(record);

        Assert.That(_store.GetSlotCount(TestItem.KeccakB.ValueHash256), Is.EqualTo(5UL));
        Assert.That(_store.GetSlotCount(TestItem.KeccakC.ValueHash256), Is.EqualTo(12UL));
    }

    [Test]
    public void ZeroNewCount_TombstonesSlotEntry()
    {
        _store.SetSlotCountForTesting(TestItem.KeccakA.ValueHash256, 7);
        Assert.That(_store.GetSlotCount(TestItem.KeccakA.ValueHash256), Is.EqualTo(7UL));

        BlockDiffRecord record = new(
            BlockNumber: 2,
            StateRoot: TestItem.KeccakD,
            CodeHashChanges: [],
            SlotCountChanges: [new SlotCountEntry(TestItem.KeccakA.ValueHash256, 7, 0)]);

        _store.WriteBlockDiff(record);

        Assert.That(_store.GetSlotCount(TestItem.KeccakA.ValueHash256), Is.EqualTo(0UL));
    }

    [Test]
    public void ReadMissingBlock_ReturnsNull() =>
        Assert.That(_store.ReadBlockDiff(999), Is.Null);

    [Test]
    public void PruneOlderThan_RemovesOnlyEntriesStrictlyBelowCutoff()
    {
        for (int i = 1; i <= 5; i++)
        {
            BlockDiffRecord record = new(i, TestItem.KeccakA, [], []);
            _store.WriteBlockDiff(record);
        }

        int removed = _store.PruneOlderThan(cutoffBlock: 3);

        Assert.That(removed, Is.EqualTo(2));
        Assert.That(_store.ReadBlockDiff(1), Is.Null);
        Assert.That(_store.ReadBlockDiff(2), Is.Null);
        Assert.That(_store.ReadBlockDiff(3), Is.Not.Null);
        Assert.That(_store.ReadBlockDiff(5), Is.Not.Null);
    }

    [Test]
    public void PruneOlderThan_DoesNotTouchSlotCounts()
    {
        _store.SetSlotCountForTesting(TestItem.KeccakA.ValueHash256, 1);
        _store.WriteBlockDiff(new BlockDiffRecord(1, TestItem.KeccakA, [], []));
        _store.WriteBlockDiff(new BlockDiffRecord(2, TestItem.KeccakA, [], []));

        _store.PruneOlderThan(cutoffBlock: 100);

        Assert.That(_store.GetSlotCount(TestItem.KeccakA.ValueHash256), Is.EqualTo(1UL));
    }
}
