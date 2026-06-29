// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
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

[TestFixture]
public class DiffsPrunerTests
{
    private MemColumnsDb<BlockDiffsColumns> _db = null!;
    private BlockDiffsStore _store = null!;
    private DiffsWriterService _writer = null!;
    private IBlockTree _blockTree = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new MemColumnsDb<BlockDiffsColumns>();
        _store = new BlockDiffsStore(_db);
        _blockTree = Substitute.For<IBlockTree>();
        _writer = new DiffsWriterService(_blockTree,
            Substitute.For<IWorldStateManager>(),
            Substitute.For<IStateReader>(),
            _store,
            LimboLogs.Instance);

        for (int i = 1; i <= 50; i++)
            _writer.WriteRecord(new BlockDiffRecord(i, TestItem.KeccakA, [], []));
    }

    [TearDown]
    public void TearDown()
    {
        _writer.Dispose();
        _db.Dispose();
    }

    [Test]
    public void PruneOnce_RemovesEntriesOlderThanWindow()
    {
        Block head = Build.A.Block.WithNumber(50).TestObject;
        _blockTree.Head.Returns(head);
        IStateDiffsWriterConfig config = new StateDiffsWriterConfig
        {
            Enabled = true,
            KeepLastNBlocks = 10,
            PruneIntervalSeconds = 600,
        };
        DiffsPruner pruner = new(_blockTree, _store, _writer, config, LimboLogs.Instance);

        // cutoff = 50 - 10 = 40, so anything strictly below 40 is removed.
        int removed = pruner.PruneOnce();

        Assert.That(removed, Is.EqualTo(39));
        Assert.That(_store.ReadBlockDiff(39), Is.Null);
        Assert.That(_store.ReadBlockDiff(40), Is.Not.Null);
        Assert.That(_store.ReadBlockDiff(50), Is.Not.Null);
    }

    [Test]
    public void PruneOnce_HonoursLastWrittenWhenHeadLags()
    {
        // BlockTree.Head can briefly lag the writer's LastWrittenBlock during startup;
        // the pruner anchors to whichever is higher to keep the window stable.
        Block staleHead = Build.A.Block.WithNumber(20).TestObject;
        _blockTree.Head.Returns(staleHead);
        IStateDiffsWriterConfig config = new StateDiffsWriterConfig
        {
            Enabled = true,
            KeepLastNBlocks = 5,
            PruneIntervalSeconds = 600,
        };
        DiffsPruner pruner = new(_blockTree, _store, _writer, config, LimboLogs.Instance);

        int removed = pruner.PruneOnce();

        // Anchor = max(20, 50) = 50; cutoff = 50 - 5 = 45.
        Assert.That(removed, Is.EqualTo(44));
        Assert.That(_store.ReadBlockDiff(44), Is.Null);
        Assert.That(_store.ReadBlockDiff(45), Is.Not.Null);
    }

    [Test]
    public void PruneOnce_NoopWhenCutoffNonPositive()
    {
        Block head = Build.A.Block.WithNumber(5).TestObject;
        _blockTree.Head.Returns(head);
        IStateDiffsWriterConfig config = new StateDiffsWriterConfig
        {
            KeepLastNBlocks = 1_000_000,
            PruneIntervalSeconds = 600,
        };
        DiffsPruner pruner = new(_blockTree, _store, _writer, config, LimboLogs.Instance);

        Assert.That(pruner.PruneOnce(), Is.Zero);
        Assert.That(_store.ReadBlockDiff(1), Is.Not.Null);
    }

    [Test]
    public void PruneOnce_NegativeKeepLastN_DisablesPruningInsteadOfDeletingEverything()
    {
        Block head = Build.A.Block.WithNumber(50).TestObject;
        _blockTree.Head.Returns(head);
        IStateDiffsWriterConfig config = new StateDiffsWriterConfig
        {
            KeepLastNBlocks = -1,
            PruneIntervalSeconds = 600,
        };
        DiffsPruner pruner = new(_blockTree, _store, _writer, config, LimboLogs.Instance);

        // A negative window must NOT wrap into a huge positive cutoff that deletes
        // every row (including the head); it disables pruning instead.
        Assert.That(pruner.PruneOnce(), Is.Zero);
        Assert.That(_store.ReadBlockDiff(1), Is.Not.Null);
        Assert.That(_store.ReadBlockDiff(50), Is.Not.Null);
    }

    [Test]
    public async Task DisposeAsync_StopsLoopAndIsSafeToDisposeAgain()
    {
        IStateDiffsWriterConfig config = new StateDiffsWriterConfig
        {
            Enabled = true,
            KeepLastNBlocks = 10,
            PruneIntervalSeconds = 600,
        };
        DiffsPruner pruner = new(_blockTree, _store, _writer, config, LimboLogs.Instance);
        pruner.Start();

        await pruner.DisposeAsync();
        // A trailing synchronous Dispose (e.g. from a different teardown path) must
        // be a harmless no-op, not a double-dispose throw.
        Assert.DoesNotThrow(pruner.Dispose);
    }
}
