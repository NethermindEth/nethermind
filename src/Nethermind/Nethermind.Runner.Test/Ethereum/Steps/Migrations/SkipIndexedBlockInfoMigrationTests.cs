// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.SkipIndexedBlockInfo;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Init.Steps.Migrations;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Ethereum.Steps.Migrations;

public class SkipIndexedBlockInfoMigrationTests
{
    [TestCase(2)]
    [TestCase(10)]
    [TestCase(64)]
    [TestCase(100)]
    public async Task Populates_cumulative_info_for_full_chain(int chainLength)
    {
        BlockTreeBuilder blockTreeBuilder = Core.Test.Builders.Build.A.BlockTree().OfChainLength(chainLength);
        IBlockTree blockTree = blockTreeBuilder.TestObject;

        MemDb cumulativeDb = new();
        SkipIndexedBlockInfoStore store = new(cumulativeDb, blockTreeBuilder.HeaderStore, new CumulativeTotalDifficultyStrategy(), NullTotalDifficultyAnchor.Instance, LimboLogs.Instance);

        ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
        syncModeSelector.Current.Returns(SyncMode.WaitingForBlock);

        SkipIndexedBlockInfoMigration migration = new(blockTree, store, cumulativeDb, syncModeSelector, NullTotalDifficultyAnchor.Instance, LimboLogs.Instance);

        await migration.Run(CancellationToken.None);

        for (long i = 0; i <= blockTree.Head!.Number; i++)
        {
            BlockHeader header = blockTree.FindHeader(i)!;
            ValueHash256 hash = header.Hash!.ValueHash256;
            UInt256? td = store.GetTotalDifficulty(i, in hash);
            td.Should().NotBeNull($"TD missing for block {i}");
        }

        long writesBefore = cumulativeDb.WritesCount;

        ValueHash256 headHash = blockTree.Head!.Hash!.ValueHash256;
        store.GetTotalDifficulty(blockTree.Head.Number, in headHash).Should().NotBeNull();

        cumulativeDb.WritesCount.Should().Be(writesBefore, "head TD lookup should not trigger further writes after migration");
    }

    [Test]
    public async Task Populates_only_post_anchor_range_when_anchor_configured()
    {
        const int chainLength = 32;
        const int pivotNumber = 10;

        BlockTreeBuilder blockTreeBuilder = Core.Test.Builders.Build.A.BlockTree().OfChainLength(chainLength);
        IBlockTree blockTree = blockTreeBuilder.TestObject;

        UInt256 pivotTd = UInt256.Zero;
        for (int i = 0; i <= pivotNumber; i++) pivotTd += blockTree.FindHeader(i)!.Difficulty;
        ValueHash256 pivotHash = blockTree.FindHeader(pivotNumber)!.Hash!.ValueHash256;

        StubAnchor anchor = new(new TotalDifficultyAnchor(pivotNumber, pivotHash, pivotTd));

        MemDb cumulativeDb = new();
        SkipIndexedBlockInfoStore store = new(cumulativeDb, blockTreeBuilder.HeaderStore, new CumulativeTotalDifficultyStrategy(), anchor, LimboLogs.Instance);

        ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
        syncModeSelector.Current.Returns(SyncMode.WaitingForBlock);

        SkipIndexedBlockInfoMigration migration = new(blockTree, store, cumulativeDb, syncModeSelector, anchor, LimboLogs.Instance);

        await migration.Run(CancellationToken.None);

        for (long i = pivotNumber; i <= blockTree.Head!.Number; i++)
        {
            BlockHeader header = blockTree.FindHeader(i)!;
            ValueHash256 hash = header.Hash!.ValueHash256;
            UInt256? td = store.GetTotalDifficulty(i, in hash);
            td.Should().NotBeNull($"TD missing for post-pivot block {i}");
        }
    }

    [Test]
    public async Task Does_nothing_when_db_not_empty()
    {
        BlockTreeBuilder blockTreeBuilder = Core.Test.Builders.Build.A.BlockTree().OfChainLength(16);
        IBlockTree blockTree = blockTreeBuilder.TestObject;

        MemDb cumulativeDb = new();
        cumulativeDb.Set([0x01], [0x02]);

        SkipIndexedBlockInfoStore store = new(cumulativeDb, blockTreeBuilder.HeaderStore, new CumulativeTotalDifficultyStrategy(), NullTotalDifficultyAnchor.Instance, LimboLogs.Instance);

        ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
        syncModeSelector.Current.Returns(SyncMode.WaitingForBlock);

        SkipIndexedBlockInfoMigration migration = new(blockTree, store, cumulativeDb, syncModeSelector, NullTotalDifficultyAnchor.Instance, LimboLogs.Instance);

        long writesBefore = cumulativeDb.WritesCount;
        await migration.Run(CancellationToken.None);
        cumulativeDb.WritesCount.Should().Be(writesBefore, "migration must not write when db already has entries");
    }

    private sealed class StubAnchor(TotalDifficultyAnchor anchor) : ITotalDifficultyAnchor
    {
        public TotalDifficultyAnchor? TryGet() => anchor;
    }
}
