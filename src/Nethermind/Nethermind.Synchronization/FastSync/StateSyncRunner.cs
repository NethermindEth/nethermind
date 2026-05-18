// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
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
    TreeSync treeSync,
    SimpleDispatcher<StateSyncBatch> stateSyncDispatcher,
    ISyncConfig syncConfig,
    ISyncModeSelector syncModeSelector,
    ISyncProgressResolver syncProgressResolver,
    IBeaconSyncStrategy beaconSyncStrategy,
    ISyncPeerPool syncPeerPool,
    IStateSyncPivot stateSyncPivot,
    ITrieReassembler trieReassembler,
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

                    if (TryReassembleAfterSnap(token))
                    {
                        return;
                    }
                }

                await RunStateSyncRounds(token);
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

    /// <summary>
    /// Attempt to rebuild the missing top of the state trie locally from the leaves snap sync
    /// committed, avoiding the network-bound healing phase. Returns <c>true</c> when reassembly
    /// produces the pivot's expected state root and finalizes the sync; <c>false</c> otherwise
    /// (the caller falls through to <see cref="RunStateSyncRounds"/>).
    /// </summary>
    private bool TryReassembleAfterSnap(CancellationToken token)
    {
        if (token.IsCancellationRequested) return false;

        BlockHeader? pivotHeader = stateSyncPivot.GetPivotHeader();
        if (pivotHeader is null)
        {
            if (_logger.IsWarn) _logger.Warn("Trie reassembly skipped: no pivot header.");
            return false;
        }

        Hash256 expectedRoot = pivotHeader.StateRoot!;

        Hash256[] updatedStorages = stateSyncPivot.UpdatedStorages.ToArray();
        if (_logger.IsInfo) _logger.Info($"Attempting local trie reassembly with {updatedStorages.Length} updated storages, target root {expectedRoot}.");

        Hash256? assembledRoot;
        try
        {
            assembledRoot = trieReassembler.TryReassemble(updatedStorages);
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Trie reassembly failed with exception, falling back to healing: {e}");
            return false;
        }

        if (assembledRoot != expectedRoot)
        {
            if (_logger.IsWarn) _logger.Warn($"Trie reassembly produced {assembledRoot ?? Keccak.Zero}, expected {expectedRoot}. Falling back to healing.");
            return false;
        }

        if (_logger.IsInfo) _logger.Info($"Trie reassembly succeeded for {expectedRoot} — skipping state healing.");
        treeSync.FinalizeSync(pivotHeader);

        if (syncConfig.VerifyTrieOnStateSyncFinished)
            verifyTrieStarter?.TryStartVerifyTrie(pivotHeader);

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
