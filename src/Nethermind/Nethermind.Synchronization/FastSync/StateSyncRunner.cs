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
                    await snapSyncRunner.Run(token);
                    if (_logger.IsInfo) _logger.Info("Snap sync completed.");
                }

                await RunStateSyncRounds(token);
            }
            finally
            {
                TuneStateDb(ITunableDb.TuneType.Default);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Clean shutdown — swallow so Synchronizer doesn't log "State sync failed".
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

            BlockHeader? roundPivot = treeSync.ResetStateRootToBestSuggested(SyncFeedState.Dormant);
            if (roundPivot is null) continue;

            await stateSyncDispatcher.Run(token);

            // If sync completed in this round, the pivot it committed against is roundPivot.
            // Capturing here avoids re-reading GetPivotHeader() (mutating) for FinalizeSync.
            if (treeSync.IsRootComplete)
            {
                finalPivot = roundPivot;
                break;
            }
        }

        if (finalPivot is null) return;

        if (_logger.IsInfo) _logger.Info("State sync completed.");

        // FinalizeSync used to be invoked inside TreeSync.SaveNode's IsRoot branch, where
        // GetPivotHeader's mutating side effect could race with concurrent root-saves
        // (svlachakis #11457/#11458). Calling it here, with the captured per-round pivot,
        // serialises the pivot read against in-flight handlers and pins the pivot value.
        treeSync.VerifyPostSyncCleanUp();
        try { treeSync.FinalizeSync(finalPivot); }
        catch (ObjectDisposedException) { }

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

        int totalSyncLag = syncConfig.StateMinDistanceFromHead + syncConfig.HeaderStateDistance;

        while (!token.IsCancellationRequested)
        {
            long header = syncProgressResolver.FindBestHeader();
            long peerBlock = 0;
            foreach (PeerInfo p in syncPeerPool.InitializedPeers)
            {
                if (p.HeadNumber > peerBlock) peerBlock = p.HeadNumber;
            }
            long targetBlock = beaconSyncStrategy.GetTargetBlockHeight() ?? peerBlock;

            if (targetBlock >= header && (targetBlock - header) <= totalSyncLag)
                return;

            await Task.Delay(1000, token);
        }
    }
}
