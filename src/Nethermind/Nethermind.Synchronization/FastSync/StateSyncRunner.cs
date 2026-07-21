// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
                    BlockHeader? firstPivot = stateSyncPivot.GetPivotHeader();
                    if (_logger.IsInfo) _logger.Info("Starting snap sync. at pivot block " + (stateSyncPivot.GetPivotHeader()?.Number.ToString() ?? "<unknown>"));
                    await snapSyncRunner.Run(token);
                    if (_logger.IsInfo) _logger.Info("Snap sync completed. at pivot block " + (stateSyncPivot.GetPivotHeader()?.Number.ToString() ?? "<unknown>"));
                        
                    if (await RunBalHealing(firstPivot, token))
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

    public async Task<bool> RunBalHealing(BlockHeader? firstPivot, CancellationToken token)
    {
        if (firstPivot is null)
        {
            if (_logger.IsInfo) _logger.Info("BAL healing skipped - no first pivot available.");
            return false;
        }

        try
        {
            Hash256? root = balHealing.Reassemble(stateSyncPivot.UpdatedStorages);
            if (root is null)
            {
                if (_logger.IsError) _logger.Error("BAL healing failed - trie reassembly failed.");
                return false;
            }

            BlockHeader currentPivot = firstPivot;

            while (true)
            {
                token.ThrowIfCancellationRequested();

                stateSyncPivot.UpdateHeaderForcefully();
                BlockHeader? nextPivot = stateSyncPivot.GetPivotHeader();
                if (nextPivot is null)
                {
                    if (_logger.IsInfo) _logger.Info("BAL healing failed - no new pivot available.");
                    return false;
                }

                if(currentPivot.Number == nextPivot.Number)
                {

                    if(stateSyncPivot.CanFinalize(currentPivot))
                    {
                        if (_logger.IsInfo) _logger.Info("BAL healing complete - no more pivots to apply.");
                        break;
                    }
                    await Task.Delay(1000, token);
                    continue;
                }

                root = balHealing.ApplyRange(root, currentPivot, nextPivot, token);
                currentPivot = nextPivot;

                if (root is null)
                {
                    return false;
                }
            }
            
            if(root != currentPivot.StateRoot)
            {
                if (_logger.IsError) _logger.Error($"BAL healing failed - produced root {root} does not match pivot state root {currentPivot.StateRoot}.");
                return false;
            }

            balHealing.FinalizeSync(currentPivot);

            if (_logger.IsInfo) _logger.Info("BAL healing completed — skipping traditional state sync.");

            if (syncConfig.VerifyTrieOnStateSyncFinished)
                verifyTrieStarter?.TryStartVerifyTrie(currentPivot);

            return true;
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a clean shutdown, not a heal failure — let the caller's handler deal with it.
            throw;
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("BAL healing failed", e);
            return false;
        }
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
