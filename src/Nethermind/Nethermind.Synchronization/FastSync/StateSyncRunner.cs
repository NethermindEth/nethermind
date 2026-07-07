// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.Synchronization.FastSync;

public class StateSyncRunner(
    ISnapSyncRunner snapSyncRunner,
    IBalHealing balHealing,
    IStateSyncPivot stateSyncPivot,
    TreeSync treeSync,
    SimpleDispatcher<StateSyncBatch> stateSyncDispatcher,
    ISyncConfig syncConfig,
    ISyncModeSelector syncModeSelector,
    ISyncProgressResolver syncProgressResolver,
    IBeaconSyncStrategy beaconSyncStrategy,
    ISyncPeerPool syncPeerPool,
    [KeyFilter(DbNames.State)] ITunableDb? stateDb,
    [KeyFilter(DbNames.Code)] ITunableDb? codeDb,
    ILogManager logManager,
    IVerifyTrieStarter? verifyTrieStarter = null) : IStateSyncRunner
{
    private readonly ILogger _logger = logManager.GetClassLogger<StateSyncRunner>();

    public async Task Run(CancellationToken token)
    {
        try
        {
            if (syncProgressResolver.FindBestFullState() != 0)
            {
                if (_logger.IsInfo) _logger.Info("State sync unnecessary - already have state.");
                return;
            }

            await StateSyncPrecursorWait(token);
            TuneStateDb(syncConfig.TuneDbMode);
            try
            {
                if (syncConfig.SnapSync)
                {
                    if (_logger.IsInfo) _logger.Info("Starting snap sync.");
                    BlockHeader? firstPivot = stateSyncPivot.GetPivotHeader();
                    await snapSyncRunner.Run(token);
                    if (_logger.IsInfo) _logger.Info("Snap sync completed.");

                    if (firstPivot is not null && await RunBalHealing(firstPivot, token))
                        return;
                }

                await RunStateSyncRounds(token);

                if (syncConfig.StaticSnapPivot && _logger.IsInfo)
                    _logger.Info($"StaticSnapPivot: state sync complete at block {syncConfig.PivotNumber} - node is idle (no further sync without a consensus client). Set Sync.ExitOnSynced=true to exit on completion.");
            }
            finally
            {
                // Skip on shutdown so we don't touch DBs that may already be disposed.
                if (!token.IsCancellationRequested) TuneStateDb(ITunableDb.TuneType.Default);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Clean shutdown — swallow so Synchronizer doesn't log "State sync failed".
        }
    }

    public async Task<bool> RunBalHealing(BlockHeader firstPivot, CancellationToken token)
    {
        stateSyncPivot.UpdateHeaderForcefully();
        BlockHeader? lastPivot = stateSyncPivot.GetPivotHeader();

        if (lastPivot is null)
        {
            if (_logger.IsInfo) _logger.Info("BAL healing skipped - no pivot header available.");
            return false;
        }

        bool healingComplete = await balHealing.Run(firstPivot, lastPivot, stateSyncPivot.UpdatedStorages, token);

        if (!healingComplete)
        {
            if (_logger.IsError) _logger.Error("BAL healing unavailable or failed — falling back to traditional state sync.");
            return false;
        }

        if (_logger.IsInfo) _logger.Info("BAL healing completed — skipping traditional state sync.");

        if (syncConfig.VerifyTrieOnStateSyncFinished)
            verifyTrieStarter?.TryStartVerifyTrie(lastPivot);

        return true;
    }

    public async Task RunStateSyncRounds(CancellationToken token)
    {
        if (_logger.IsInfo) _logger.Info("Starting state sync.");

        BlockHeader? finalPivot = null;

        while (!token.IsCancellationRequested)
        {
            // Yield between rounds when the mode selector has moved away from StateNodes
            // (e.g. beacon control, UpdatingPivot, fast-sync re-entry) or when we've drifted
            // away from head, so those phases can claim peers. Returns immediately if already
            // in StateNodes mode and close to head.
            await StateSyncPrecursorWait(token);

            BlockHeader? roundPivot = treeSync.ResetStateRootToBestSuggested();
            if (roundPivot is null)
            {
                // Pivot not known yet — wait and retry. StateSyncPrecursorWait can return
                // immediately, so without this we'd spin tightly.
                await Task.Delay(1000, token);
                continue;
            }

            await stateSyncDispatcher.Run(token);

            // If sync completed in this round, the pivot it committed against is roundPivot.
            // Capturing here avoids re-reading GetPivotHeader() (mutating) for FinalizeSync.
            if (treeSync.CanFinalize(roundPivot))
            {
                finalPivot = roundPivot;
                break;
            }
        }

        if (finalPivot is null) return;

        if (_logger.IsInfo) _logger.Info($"STATE SYNC FINISHED:{Metrics.StateSyncRequests}, {Metrics.SyncedStateTrieNodes}");

        treeSync.VerifyPostSyncCleanUp();
        treeSync.FinalizeSync(finalPivot);

        if (syncConfig.VerifyTrieOnStateSyncFinished)
            verifyTrieStarter?.TryStartVerifyTrie(finalPivot);
    }

    private void TuneStateDb(ITunableDb.TuneType tuneType)
    {
        stateDb?.Tune(tuneType);
        codeDb?.Tune(tuneType);
    }

    private async Task StateSyncPrecursorWait(CancellationToken token)
    {
        await syncModeSelector.WaitUntilMode(m => (m & SyncMode.StateNodes) != 0, token);

        if (syncConfig.StaticSnapPivot) return;

        ulong totalSyncLag = syncConfig.StateMinDistanceFromHead + syncConfig.HeaderStateDistance;

        while (!token.IsCancellationRequested)
        {
            ulong header = syncProgressResolver.FindBestHeader();
            ulong peerBlock = 0;
            foreach (PeerInfo p in syncPeerPool.InitializedPeers)
            {
                ulong peerHeadNumber = p.HeadNumber;
                if (peerHeadNumber > peerBlock) peerBlock = peerHeadNumber;
            }
            ulong targetBlock = beaconSyncStrategy.GetTargetBlockHeight() ?? peerBlock;

            if (targetBlock >= header && (targetBlock - header) <= totalSyncLag)
                return;

            await Task.Delay(1000, token);
        }
    }
}
