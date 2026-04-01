// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.AttributeFilters;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>
/// Regression tests for the infinite beacon sync restart loop (NethermindEth/nethermind#6304, #6611).
/// Exercises the production sync path using a DI container with the full merge sync stack.
/// </summary>
public partial class BlockTreeTests
{
    /// <summary>
    /// Builds a remote chain used as the "peer" — source of truth for headers.
    /// </summary>
    private static BlockTree BuildRemoteChain(int length)
    {
        TestSpecProvider specProvider = new(London.Instance);
        specProvider.TerminalTotalDifficulty = 0;
        return Build.A.BlockTree(Build.A.Block.Genesis.TestObject, specProvider)
            .WithoutSettingHead
            .OfChainLength(length)
            .TestObject;
    }

    /// <summary>
    /// Drives the FastSyncFeed dispatcher briefly, then cancels it.
    /// </summary>
    private static async Task RunFastSyncFeedBriefly(SyncFeedComponent<BlocksRequest> component, int durationMs = 500)
    {
        using CancellationTokenSource cts = new(5000);
        Task dispatcherTask = component.Dispatcher.Start(cts.Token);
        component.Feed.Activate();

        await Task.Delay(durationMs, cts.Token);

        component.Feed.Finish();
        await cts.CancelAsync();
        try { await dispatcherTask; } catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Sets up the local block tree and beacon headers, then returns a DI container with the full sync stack wired.
    /// </summary>
    private static async Task<(IContainer container, BlockTree localTree)> SetupSyncScenario(
        BlockTree remoteChain,
        long pivotNumber,
        long beaconPivotNumber,
        int localChainLength,
        bool driveStartingSyncPivotUpdater = true,
        int missingBeaconHeaderSafetyTimeoutSec = 30)
    {
        BlockHeader pivotHeader = remoteChain.FindHeader(pivotNumber, BlockTreeLookupOptions.None)!;

        TestSpecProvider specProvider = new(London.Instance);
        specProvider.TerminalTotalDifficulty = 0;
        ISyncConfig syncConfig = new SyncConfig { FastSync = true, MaxAttemptsToUpdatePivot = 1, MissingBeaconHeaderSafetyTimeoutSec = missingBeaconHeaderSafetyTimeoutSec };

        BlockTreeBuilder localTreeBuilder = Build.A.BlockTree(Build.A.Block.Genesis.TestObject, specProvider)
            .WithSyncConfig(syncConfig)
            .OfChainLength(localChainLength);
        BlockTree localTree = localTreeBuilder.TestObject;

        if (driveStartingSyncPivotUpdater)
        {
            // Mock peer returns the pivot header when StartingSyncPivotUpdater asks
            ISyncPeer fakePeer = Substitute.For<ISyncPeer>();
            fakePeer.GetHeadBlockHeader(Arg.Any<Hash256>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<BlockHeader?>(pivotHeader));
            fakePeer.Node.Returns(new Node(new NetworkNode(TestItem.PublicKeyA, "127.0.0.1", 30303, 100L)));

            ISyncPeerPool updaterPeerPool = Substitute.For<ISyncPeerPool>();
            updaterPeerPool.InitializedPeers.Returns(new[] { new PeerInfo(fakePeer) });

            ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
            IBeaconSyncStrategy beaconSyncStrategy = Substitute.For<IBeaconSyncStrategy>();
            beaconSyncStrategy.GetFinalizedHash().Returns(pivotHeader.Hash);

            // Create the updater — production code
            _ = new StartingSyncPivotUpdater(
                localTree, syncModeSelector, updaterPeerPool, syncConfig,
                new BlockCacheService(), beaconSyncStrategy, LimboLogs.Instance);

            // Fire OnSyncModeChanged → TrySetFreshPivot → UpdateConfigValues (production code)
            syncModeSelector.Changed += Raise.EventWith(
                new SyncModeChangedEventArgs(SyncMode.FastSync, SyncMode.UpdatingPivot));

            // Give async handler time to complete
            await Task.Delay(300);

            localTree.SyncPivot.BlockNumber.Should().Be(pivotNumber,
                "StartingSyncPivotUpdater should have set the pivot");
        }

        // Insert beacon headers [N+1, M-1] — simulating BeaconHeadersSyncFeed output
        for (long i = beaconPivotNumber - 1; i >= pivotNumber + 1; i--)
        {
            BlockHeader header = remoteChain.FindHeader(i, BlockTreeLookupOptions.None)!;
            localTree.Insert(header, BlockTreeInsertHeaderOptions.BeaconHeaderInsert);
        }

        // Wire DI container with full merge sync stack
        IConfigProvider configProvider = new ConfigProvider(
            new MergeConfig { TerminalTotalDifficulty = "0" }, syncConfig);

        IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(configProvider))
            .AddModule(new TestMergeModule(configProvider))
            .AddSingleton<IBlockTree>(localTree)
            .AddKeyedSingleton<IDb>(DbNames.Metadata, localTreeBuilder.MetadataDb)
            .AddSingleton<ISyncPeerPool>(Substitute.For<ISyncPeerPool>())
            .AddSingleton<SyncLoopContext>()
            .Build();

        // Set beacon pivot — simulating ForkchoiceUpdatedHandler.EnsurePivot
        BlockHeader beaconPivotHeader = remoteChain.FindHeader(beaconPivotNumber, BlockTreeLookupOptions.None)!;
        SyncLoopContext ctx = container.Resolve<SyncLoopContext>();
        ctx.BeaconPivot.EnsurePivot(beaconPivotHeader);
        ctx.BeaconPivot.ProcessDestination = beaconPivotHeader;

        return (container, localTree);
    }

    // =====================================================================
    // Regression test: FAILS on unfixed code, PASSES on fixed code
    // =====================================================================

    /// <summary>
    /// Regression test for NethermindEth/nethermind#6304, #6611.
    ///
    /// Setup: pivot N=4, beacon headers [5,11], beacon pivot 12.
    /// ChainLevelHelper walks to block 4 which may be missing (no ChainLevelInfo from
    /// UpdateConfigValues). The feed-aware OnMissingBeaconHeader guard with safety timer
    /// should prevent an immediate forced restart — block 4 is in the FastHeaders range
    /// and the timer hasn't expired yet.
    /// </summary>
    [Test]
    public async Task Pivot_header_inserted_by_UpdateConfigValues_prevents_beacon_sync_restart_loop()
    {
        BlockTree remoteChain = BuildRemoteChain(16);

        (IContainer container, _) = await SetupSyncScenario(
            remoteChain, pivotNumber: 4, beaconPivotNumber: 12, localChainLength: 4);

        await using (container)
        {
            SyncLoopContext ctx = container.Resolve<SyncLoopContext>();
            await RunFastSyncFeedBriefly(ctx.FastSyncComponent);

            ctx.BeaconPivot.ShouldForceStartNewSync.Should().BeFalse(
                "The feed-aware OnMissingBeaconHeader guard should not force restart — " +
                "block 4 is in the FastHeaders range and the safety timer hasn't expired.");
        }
    }

    /// <summary>
    /// Same regression test at genesis boundary: pivot at block 1, local chain has genesis only.
    /// </summary>
    [Test]
    public async Task Pivot_header_at_genesis_boundary_prevents_beacon_sync_restart_loop()
    {
        BlockTree remoteChain = BuildRemoteChain(12);

        (IContainer container, _) = await SetupSyncScenario(
            remoteChain, pivotNumber: 1, beaconPivotNumber: 8, localChainLength: 1);

        await using (container)
        {
            SyncLoopContext ctx = container.Resolve<SyncLoopContext>();
            await RunFastSyncFeedBriefly(ctx.FastSyncComponent);

            ctx.BeaconPivot.ShouldForceStartNewSync.Should().BeFalse(
                "Same guard verification at genesis boundary — pivot at block 1");
        }
    }

    // =====================================================================
    // Genuine gap: restart fires even WITH the fix
    // =====================================================================

    /// <summary>
    /// Pivot at block 6 with header inserted, but blocks 4-5 are genuinely missing.
    /// ChainLevelHelper should still fire OnMissingBeaconHeader for this real gap.
    /// </summary>
    [Test]
    public async Task Genuine_gap_between_regular_chain_and_beacon_range_still_triggers_restart()
    {
        BlockTree remoteChain = BuildRemoteChain(16);
        long pivotNumber = 6;
        long beaconPivotNumber = 12;

        // Set up WITHOUT driving StartingSyncPivotUpdater — manually set pivot with header insert
        // to isolate the test from the updater. Blocks 4-5 are intentionally missing.
        (IContainer container, BlockTree localTree) = await SetupSyncScenario(
            remoteChain, pivotNumber, beaconPivotNumber, localChainLength: 4,
            driveStartingSyncPivotUpdater: false, missingBeaconHeaderSafetyTimeoutSec: 0);

        // Manually set pivot with header (simulating fixed UpdateConfigValues)
        BlockHeader pivotHeader = remoteChain.FindHeader(pivotNumber, BlockTreeLookupOptions.None)!;
        localTree.SyncPivot = (pivotNumber, pivotHeader.Hash!);
        localTree.Insert(pivotHeader, BlockTreeInsertHeaderOptions.BeaconHeaderInsert | BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);

        await using (container)
        {
            SyncLoopContext ctx = container.Resolve<SyncLoopContext>();
            await RunFastSyncFeedBriefly(ctx.FastSyncComponent);

            ctx.BeaconPivot.ShouldForceStartNewSync.Should().BeTrue(
                "Blocks 4-5 are genuinely missing between regular chain (0-3) and beacon range (6-12). " +
                "This is a real gap that should trigger restart even with the fix applied.");
        }
    }

    // =====================================================================
    // Infrastructure
    // =====================================================================

    private record SyncLoopContext(
        IBeaconPivot BeaconPivot,
        [KeyFilter(nameof(FastSyncFeed))] SyncFeedComponent<BlocksRequest> FastSyncComponent,
        IBlockTree BlockTree
    );
}
