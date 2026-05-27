// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using Nethermind.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.StateDiffsWriter.Data;
using Nethermind.StateDiffsWriter.Service;
using Nethermind.StateDiffsWriter.Storage;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.StateDiffsWriter.Test.Service;

/// <summary>
/// End-to-end persistence behaviour of <see cref="DiffsWriterService"/> driven
/// through the public <see cref="DiffsWriterService.WriteRecord"/> seam:
/// constructing 100 synthetic <see cref="BlockDiffRecord"/> instances, feeding
/// them through the service, and verifying every record round-trips out of the
/// underlying <see cref="MemColumnsDb{T}"/>. The Diff computation itself is
/// covered by <see cref="DiffsWriterWalkerTests"/>; what we want to exercise
/// here is the atomic CF write + slot-count carry forward + last-block tracking.
/// </summary>
[TestFixture]
public class DiffsWriterServiceTests
{
    private MemColumnsDb<BlockDiffsColumns> _db = null!;
    private BlockDiffsStore _store = null!;
    private DiffsWriterService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new MemColumnsDb<BlockDiffsColumns>();
        _store = new BlockDiffsStore(_db);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IWorldStateManager worldStateManager = Substitute.For<IWorldStateManager>();
        IStateReader stateReader = Substitute.For<IStateReader>();
        _service = new DiffsWriterService(blockTree, worldStateManager, stateReader, _store, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _service.Dispose();
        _db.Dispose();
    }

    [Test]
    public void WriteRecord_PersistsEveryBlock()
    {
        for (int i = 1; i <= 100; i++)
        {
            BlockDiffRecord record = new(
                BlockNumber: i,
                StateRoot: TestItem.KeccakA,
                CodeHashChanges: [],
                SlotCountChanges:
                [
                    new SlotCountEntry(TestItem.KeccakB.ValueHash256, OldCount: (ulong)(i - 1), NewCount: (ulong)i)
                ]);
            _service.WriteRecord(record);
        }

        for (int i = 1; i <= 100; i++)
        {
            BlockDiffRecord? read = _store.ReadBlockDiff(i);
            Assert.That(read, Is.Not.Null, $"block {i}");
            Assert.That(read!.BlockNumber, Is.EqualTo(i));
            Assert.That(read.SlotCountChanges, Has.Count.EqualTo(1));
            Assert.That(read.SlotCountChanges[0].NewCount, Is.EqualTo((ulong)i));
        }

        Assert.That(_service.LastWrittenBlock, Is.EqualTo(100));
        Assert.That(_store.GetSlotCount(TestItem.KeccakB.ValueHash256), Is.EqualTo(100UL));
    }

    [Test]
    public void WriteRecord_TracksLastWrittenBlock()
    {
        Assert.That(_service.LastWrittenBlock, Is.EqualTo(-1));

        _service.WriteRecord(new BlockDiffRecord(7, TestItem.KeccakA, [], []));
        Assert.That(_service.LastWrittenBlock, Is.EqualTo(7));

        _service.WriteRecord(new BlockDiffRecord(8, TestItem.KeccakA, [], []));
        Assert.That(_service.LastWrittenBlock, Is.EqualTo(8));
    }
}
