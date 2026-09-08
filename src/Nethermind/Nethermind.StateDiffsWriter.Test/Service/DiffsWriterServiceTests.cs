// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.StateDiff.Core.Data;
using Nethermind.StateDiffsWriter.Data;
using Nethermind.StateDiffsWriter.Service;
using Nethermind.StateDiffsWriter.Storage;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.StateDiffsWriter.Test.Service;

/// <summary>
/// Persistence behaviour of <see cref="DiffsWriterService"/> through the <see cref="DiffsWriterService.WriteRecord"/>
/// seam; diff computation is covered by <see cref="DiffsWriterWalkerTests"/>.
/// </summary>
[TestFixture]
public class DiffsWriterServiceTests
{
    private MemColumnsDb<BlockDiffsColumns> _db = null!;
    private BlockDiffsStore _store = null!;
    private IBlockTree _blockTree = null!;
    private IStateReader _stateReader = null!;
    private DiffsWriterService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new MemColumnsDb<BlockDiffsColumns>();
        _store = new BlockDiffsStore(_db);
        _blockTree = Substitute.For<IBlockTree>();
        _stateReader = Substitute.For<IStateReader>();
        _service = new DiffsWriterService(
            _blockTree, Substitute.For<IWorldStateManager>(), _stateReader, _store, LimboLogs.Instance);
    }

    private void RaiseNewHead(Block block) =>
        _blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(block));

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

    [Test]
    public void OnNewHeadBlock_HeadNotBuildingOnLastWritten_CountsReorg()
    {
        // Parent reports the block's own state root, so the service takes the no-op path but still runs reorg detection.
        BlockHeader parent = Build.A.BlockHeader.WithStateRoot(TestItem.KeccakA).TestObject;
        _blockTree.FindHeader(Arg.Any<ulong>(), Arg.Any<BlockTreeLookupOptions>()).Returns(parent);

        Block b10 = Build.A.Block.WithNumber(10).WithStateRoot(TestItem.KeccakA).TestObject;
        RaiseNewHead(b10);
        Assert.That(_service.ReorgsObserved, Is.Zero, "the first head cannot be a reorg");

        Block b11 = Build.A.Block.WithNumber(11).WithParent(b10).WithStateRoot(TestItem.KeccakA).TestObject;
        RaiseNewHead(b11);
        Assert.That(_service.ReorgsObserved, Is.Zero, "a head building on the last-written block is contiguous");

        // Reorg: a competing block at height 11 that builds on b10, not the now-stale b11.
        Block b11Prime = Build.A.Block.WithNumber(11).WithParent(b10)
            .WithExtraData([0x01]).WithStateRoot(TestItem.KeccakA).TestObject;
        RaiseNewHead(b11Prime);
        Assert.That(_service.ReorgsObserved, Is.EqualTo(1));
    }

    [Test]
    public void ResolveNewCodeSize_NoCode_ReturnsZero()
    {
        CodeHashChange noCode = new(TestItem.KeccakA.ValueHash256, CodeHashChange.NoCode, CodeHashChange.NoCode);
        Assert.That(_service.ResolveNewCodeSize(noCode, blockNumber: 1), Is.Zero);
    }

    [Test]
    public void ResolveNewCodeSize_PresentCode_ReturnsLength()
    {
        _stateReader.GetCode(default(ValueHash256)).ReturnsForAnyArgs(new byte[24]);
        CodeHashChange gained = new(TestItem.KeccakA.ValueHash256, CodeHashChange.NoCode, TestItem.KeccakB.ValueHash256);
        Assert.That(_service.ResolveNewCodeSize(gained, blockNumber: 1), Is.EqualTo(24u));
    }

    [Test]
    public void ResolveNewCodeSize_LiveHashButMissingCode_ReturnsZero()
    {
        // GetCode returns null by default, simulating pruned/lagging code where the hash exists but bytes are gone.
        CodeHashChange gained = new(TestItem.KeccakA.ValueHash256, CodeHashChange.NoCode, TestItem.KeccakB.ValueHash256);
        Assert.That(_service.ResolveNewCodeSize(gained, blockNumber: 42), Is.Zero);
    }

    [Test]
    public void Dispose_IsIdempotent()
    {
        _service.Dispose();
        Assert.DoesNotThrow(() => _service.Dispose());
    }
}
