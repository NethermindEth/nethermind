// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
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
    StateHealingStrategy healingStrategy,
    BalFetcher balFetcher,
    IStateSyncPivot stateSyncPivot,
    IBlockTree blockTree,
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
                    BlockHeader firstPivot = await WaitForPivot(token);
                    healingStrategy.SetPivot(firstPivot);

                    if (healingStrategy.CanBalHeal)
                        await RunSnapSyncWithBalHealing(firstPivot, token);
                    else
                        await RunSnapSync(token);
                }

                if (!healingStrategy.CanBalHeal)
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

    private async Task<BlockHeader> WaitForPivot(CancellationToken token)
    {
        BlockHeader? pivot = stateSyncPivot.GetPivotHeader();
        while (pivot is null)
        {
            await Task.Delay(1000, token);
            pivot = stateSyncPivot.GetPivotHeader();
        }

        return pivot;
    }

    private async Task RunSnapSync(CancellationToken token)
    {
        if (_logger.IsInfo) _logger.Info("Starting snap sync.");
        await snapSyncRunner.Run(token);
        if (_logger.IsInfo) _logger.Info("Snap sync completed.");
    }

    /// <remarks>A deep reorg orphans the synced state, so it is discarded and synced again from a canonical pivot.</remarks>
    public async Task RunSnapSyncWithBalHealing(BlockHeader pivot, CancellationToken token)
    {
        while (true)
        {
            await RunSnapSync(token);

            try
            {
                await RunBalHealing(pivot, token);
                return;
            }
            catch (PivotReorgedException e)
            {
                if (_logger.IsWarn) _logger.Warn($"{e.Message} Discarding the synced state and starting over.");

                pivot = await WaitForPivot(token);
                while (!blockTree.IsMainChain(pivot))
                {
                    await Task.Delay(1000, token);
                    pivot = await WaitForPivot(token);
                }
            }
        }
    }

    public async Task RunBalHealing(BlockHeader firstPivot, CancellationToken token)
    {
        if (_logger.IsInfo) _logger.Info($"Starting BAL healing from block {firstPivot.Number}.");

        Hash256 root = balHealing.Reassemble(stateSyncPivot.UpdatedStorages, token)
            ?? throw new InvalidOperationException("BAL healing failed - trie reassembly produced no base state root.");

        BlockHeader currentPivot = firstPivot;
        BlockHeader? finalPivot = null;

        while (!token.IsCancellationRequested)
        {
            await StateSyncPrecursorWait(token);

            if (!blockTree.IsMainChain(currentPivot)) throw new PivotReorgedException(currentPivot);

            stateSyncPivot.UpdateHeaderForcefully();
            BlockHeader roundPivot = stateSyncPivot.GetPivotHeader() ?? throw new InvalidOperationException("BAL healing failed - no new pivot available.");

            // Healed up to the latest pivot — wait for it to move on, unless it is final and healing is done.
            // The pivot can also move back below it, which leaves nothing to apply; a reorg behind that is caught
            // by the main chain check above once the block tree settles.
            if (roundPivot.Number <= currentPivot.Number)
            {
                if (currentPivot.Hash == roundPivot.Hash && stateSyncPivot.CanFinalize(currentPivot))
                {
                    finalPivot = currentPivot;
                    break;
                }

                await Task.Delay(1000, token);
                continue;
            }

            if (!await balFetcher.EnsureRange(currentPivot, roundPivot, token))
            {
                if (_logger.IsDebug) _logger.Debug($"BAL healing stalled - no BALs for blocks {currentPivot.Number + 1}..{roundPivot.Number} yet, retrying.");
                await Task.Delay(1000, token);
                continue;
            }

            (bool baseRootIntact, Hash256? newRoot) = balHealing.ApplyRange(root, currentPivot, roundPivot, token);

            // The round is collected by block number, so a reorg under either end of it invalidates the result.
            if (!blockTree.IsMainChain(currentPivot)) throw new PivotReorgedException(currentPivot);
            if (!blockTree.IsMainChain(roundPivot)) throw new PivotReorgedException(roundPivot);

            if (newRoot is null)
            {
                if (!baseRootIntact)
                    throw new InvalidOperationException($"BAL healing failed - could not apply BALs for blocks {currentPivot.Number + 1}..{roundPivot.Number}.");

                if (_logger.IsDebug) _logger.Debug($"BAL healing stalled - BALs for blocks {currentPivot.Number + 1}..{roundPivot.Number} went missing, retrying.");
                await Task.Delay(1000, token);
                continue;
            }

            root = newRoot;
            currentPivot = roundPivot;
        }

        if (finalPivot is null) return;

        if (!blockTree.IsMainChain(finalPivot)) throw new PivotReorgedException(finalPivot);

        if (root != finalPivot.StateRoot)
        {
            throw new InvalidOperationException($"BAL healing failed - produced root {root} does not match pivot state root {finalPivot.StateRoot}.");
        }

        if (_logger.IsInfo) _logger.Info($"BAL healing finished at block {finalPivot.Number}");

        balHealing.FinalizeSync(finalPivot);

        if (syncConfig.VerifyTrieOnStateSyncFinished)
            verifyTrieStarter?.TryStartVerifyTrie(finalPivot);
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

    private sealed class PivotReorgedException(BlockHeader pivot)
        : Exception($"State sync pivot {pivot.Number} ({pivot.Hash}) was reorged out.");

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
