// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Config;
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
    [TestCase(16UL, SyncBatchSizeMax * 8UL, 32, 32UL, 3UL, 32UL)]
    [TestCase(16UL, SyncBatchSizeMax * 8UL, 32, 29UL, 3UL, 29UL)]
    [TestCase(16UL, SyncBatchSizeMax * 8UL, 0, 32UL, 3UL, 32UL)]
    [TestCase(16UL, SyncBatchSizeMax * 8UL, 32, 32UL, 3UL, 32UL)]
    [TestCase(16UL, SyncBatchSizeMax * 8UL, 32, 32UL, 3UL, 32UL)]
    [TestCase(16UL, SyncBatchSizeMax * 8UL, 32, SyncBatchSizeMax * 8UL - 16UL, 3UL, 130UL)]
    public async Task Merge_Happy_path(ulong beaconPivot, ulong headNumber, int threshold, ulong insertedBeaconBlocks, ulong expectedFirstBlock, ulong expectedLastBlock)
    {
        ulong notSyncedTreeStartingBlockNumber = 3;

        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(notSyncedTreeStartingBlockNumber + 1, headNumber + 1)
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
        Assert.That(headers?[0]?.Number, Is.EqualTo(expectedFirstBlock));
        Assert.That(headers?[^1]?.Number, Is.EqualTo(expectedLastBlock));
    }

    [TestCase(32UL, DownloaderOptions.Insert, 16, false, 16L)]
    [TestCase(32UL, DownloaderOptions.Insert, 16, true, null)] // No beacon header, so it does not sync
    public async Task IfNoBeaconPivot_thenStopAtPoS(ulong headNumber, int options, int ttdBlock, bool withBeaconPivot, long? expectedBestKnownNumber)
    {
        UInt256 ttd = 10_000_000;
        int negativeTd = BlockHeaderBuilder.DefaultDifficulty.ToInt32(null);
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(
                4,
                headNumber + 1,
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
            ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(16UL, BlockTreeLookupOptions.None));

        SyncPeerMock syncPeer = new(syncedTree, false, Response.AllCorrect, 16000000);

        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;
        ctx.ConfigureBestPeer(syncPeer);
        using IOwnedReadOnlyList<BlockHeader?>? headers = await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None);
        Assert.That(headers?[^1]?.Number, Is.EqualTo(expectedBestKnownNumber));


    }

    [TestCase(32UL, 32UL, 0UL, 32UL)]
    [TestCase(32UL, 32UL, 10UL, 22UL)]
    public async Task WillSkipBlocksToIgnore(ulong pivot, ulong headNumber, ulong blocksToIgnore, ulong expectedBestKnownNumber)
    {
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(4, headNumber + 1)
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
        Assert.That(headers?[^1]?.Number, Is.EqualTo(expectedBestKnownNumber));
    }

    private IContainer CreateMergeNode(Action<ContainerBuilder>? configurer = null, params IConfig[] configs)
    {
        IConfigProvider configProvider = new ConfigProvider(configs);
        return CreateNode((builder) =>
        {
            builder
                .AddModule(new TestMergeModule(configProvider))
                .AddSingleton<PostMergeContext>();
            configurer?.Invoke(builder);
        }, configProvider);
    }

    private IContainer CreateMergeNode(BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder treeBuilder, params IConfig[] configs) =>
        CreateMergeNode((builder) =>
        {
            builder
                .AddSingleton<IBlockTree>(treeBuilder.NotSyncedTree)
                .AddKeyedSingleton<IDb>(DbNames.Metadata, treeBuilder.NotSyncedTreeBuilder.MetadataDb);
        }, configs);

    private record PostMergeContext(
        IBeaconPivot BeaconPivot,
        ResponseBuilder ResponseBuilder,
        IForwardHeaderProvider ForwardHeaderProvider,
        IBlockTree BlockTree,
        ISyncPeerPool PeerPool
    ) : Context(ResponseBuilder, ForwardHeaderProvider, BlockTree, PeerPool);
}
