// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.Peers;
using NUnit.Framework;
namespace Nethermind.Synchronization.Test;

public partial class ForwardHeaderProviderTests
{
    [TestCase(16L, 32L, 32, 32, 3, 32)]
    [TestCase(16L, 32L, 32, 29, 3, 29)]
    [TestCase(16L, 32L, 0, 32, 3, 32)]
    [TestCase(16L, SyncBatchSizeMax * 8, 32, 32, 3, 32)]
    [TestCase(16L, SyncBatchSizeMax * 8, 32, 32, 3, 32)]
    [TestCase(16L, SyncBatchSizeMax * 8, 32, SyncBatchSizeMax * 8 - 16L, 3, 130)]
    public async Task Merge_Happy_path(long beaconPivot, long headNumber, int threshold, long insertedBeaconBlocks, long expectedFirstBlock, long expectedLastBlock)
    {
        int notSyncedTreeStartingBlockNumber = 3;

        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(notSyncedTreeStartingBlockNumber + 1, (int)headNumber + 1)
            .InsertBeaconPivot(beaconPivot)
            .InsertBeaconHeaders(notSyncedTreeStartingBlockNumber + 1, beaconPivot - 1)
            .InsertBeaconBlocks(beaconPivot + 1, insertedBeaconBlocks, BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Null);
        BlockTree syncedTree = blockTrees.SyncedTree;

        await using IContainer container = CreateMergeNode(blockTrees, new MergeConfig()
        {
            TerminalTotalDifficulty = "0"
        });
        PostMergeContext ctx = container.Resolve<PostMergeContext>();

        ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(beaconPivot, BlockTreeLookupOptions.None));
        ctx.BeaconPivot.ProcessDestination = blockTrees.SyncedTree.FindHeader(beaconPivot, BlockTreeLookupOptions.None);

        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;
        SyncPeerMock syncPeer = new(syncedTree, false, Response.AllCorrect, 16000000);
        ctx.ConfigureBestPeer(syncPeer);
        using IOwnedReadOnlyList<BlockHeader?>? headers = await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None);
        headers?[0]?.Number.Should().Be(expectedFirstBlock);
        headers?[^1]?.Number.Should().Be(expectedLastBlock);
    }

    [TestCase(32L, DownloaderOptions.Insert, 16, false, 16)]
    [TestCase(32L, DownloaderOptions.Insert, 16, true, 3)] // No beacon header, so it does not sync
    public async Task IfNoBeaconPivot_thenStopAtPoS(long headNumber, int options, int ttdBlock, bool withBeaconPivot, int expectedBestKnownNumber)
    {
        UInt256 ttd = 10_000_000;
        int negativeTd = BlockHeaderBuilder.DefaultDifficulty.ToInt32(null);
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(
                4,
                (int)headNumber + 1,
                true,
                ttd,
                syncedSplitFrom: ttdBlock,
                syncedSplitVariant: negativeTd
            );
        BlockTree syncedTree = blockTrees.SyncedTree;

        await using IContainer container = CreateMergeNode(blockTrees, new MergeConfig()
        {
            TerminalTotalDifficulty = $"{ttd}"
        });
        PostMergeContext ctx = container.Resolve<PostMergeContext>();

        if (withBeaconPivot)
            ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(16, BlockTreeLookupOptions.None));

        SyncPeerMock syncPeer = new(syncedTree, false, Response.AllCorrect, 16000000);
        PeerInfo peerInfo = new(syncPeer);

        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;
        ctx.ConfigureBestPeer(syncPeer);
        using IOwnedReadOnlyList<BlockHeader?>? headers = await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None);
        headers?[^1]?.Number.Should().Be(expectedBestKnownNumber);


    }

    [TestCase(32L, 32L, 0, 32)]
    [TestCase(32L, 32L, 10, 22)]
    public async Task WillSkipBlocksToIgnore(long pivot, long headNumber, int blocksToIgnore, long expectedBestKnownNumber)
    {
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(4, (int)headNumber + 1)
            .InsertBeaconPivot(pivot)
            .InsertBeaconHeaders(4, pivot - 1);

        BlockTree syncedTree = blockTrees.SyncedTree;
        await using IContainer container = CreateMergeNode(blockTrees);
        PostMergeContext ctx = container.Resolve<PostMergeContext>();

        ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(pivot, BlockTreeLookupOptions.None));
        ctx.BeaconPivot.ProcessDestination = blockTrees.SyncedTree.FindHeader(pivot, BlockTreeLookupOptions.None);

        Response responseOptions = Response.AllCorrect;

        SyncPeerMock syncPeer = new(syncedTree, false, responseOptions, 16000000);
        PeerInfo peerInfo = new(syncPeer);

        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;
        ctx.ConfigureBestPeer(peerInfo);
        using IOwnedReadOnlyList<BlockHeader?>? headers = await forwardHeader.GetBlockHeaders(blocksToIgnore, 128, CancellationToken.None);
        headers?[^1]?.Number.Should().Be(expectedBestKnownNumber);
    }

    private IContainer CreateMergeNode(Action<ContainerBuilder>? configurer = null, params IConfig[] configs)
    {
        IConfigProvider configProvider = new ConfigProvider(configs);
        return CreateNode((builder) =>
        {
            builder
                .AddModule(new MergeModule(configProvider))
                .AddSingleton<PostMergeContext>();
            configurer?.Invoke(builder);
        }, configProvider);
    }

    private IContainer CreateMergeNode(BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder treeBuilder, params IConfig[] configs)
    {
        return CreateMergeNode((builder) =>
        {
            builder
                .AddSingleton<IBlockTree>(treeBuilder.NotSyncedTree)
                .AddKeyedSingleton<IDb>(DbNames.Metadata, treeBuilder.NotSyncedTreeBuilder.MetadataDb);
        }, configs);
    }

    private record PostMergeContext(
        IBeaconPivot BeaconPivot,
        ResponseBuilder ResponseBuilder,
        IForwardHeaderProvider ForwardHeaderProvider,
        IBlockTree BlockTree,
        ISyncPeerPool PeerPool
    ) : Context(ResponseBuilder, ForwardHeaderProvider, BlockTree, PeerPool);
}
