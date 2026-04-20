// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Synchronization.StateSync;

namespace Nethermind.Synchronization.FastSync;

public class StateSyncRunner(
    ISnapSyncRunner snapSyncRunner,
    TreeSync treeSync,
    IStateSyncPivot stateSyncPivot,
    StateSyncFeed stateSyncFeed,
    StateSyncDownloader stateSyncDownloader,
    StateSyncAllocationStrategyFactory stateSyncAllocationStrategyFactory,
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
        if (syncProgressResolver.FindBestFullState() != 0)
        {
            if (_logger.IsInfo) _logger.Info("State sync unnecessary - already have state.");
            return;
        }

        await syncModeSelector.WaitUntilMode(m => (m & SyncMode.StateNodes) != 0, token);
        await WaitForCloseToHead(token);
        TuneStateDb(syncConfig.TuneDbMode);
        try
        {
            if (syncConfig.SnapSync)
            {
                if (_logger.IsInfo) _logger.Info("Starting snap sync.");
                await snapSyncRunner.Run(token);
                if (_logger.IsInfo) _logger.Info("Snap sync completed.");
            }

            await RunRound(token);
        }
        finally
        {
            TuneStateDb(ITunableDb.TuneType.Default);
        }
    }

    public async Task RunRound(CancellationToken token)
    {
        if (_logger.IsInfo) _logger.Info("Starting state sync.");
        SimpleDispatcher dispatcher = new(syncPeerPool, syncConfig, logManager);

        while (!token.IsCancellationRequested)
        {
            treeSync.ResetStateRootToBestSuggested(SyncFeedState.Dormant);
            await dispatcher.RunFeed(
                stateSyncFeed,
                stateSyncDownloader,
                stateSyncAllocationStrategyFactory,
                AllocationContexts.State,
                token);

            if (treeSync.IsRootComplete) break;
        }

        if (_logger.IsInfo) _logger.Info("State sync completed.");

        if (syncConfig.VerifyTrieOnStateSyncFinished && stateSyncPivot.GetPivotHeader() is { } pivot)
        {
            verifyTrieStarter?.TryStartVerifyTrie(pivot);
        }
    }

    private void TuneStateDb(ITunableDb.TuneType tuneType)
    {
        stateDb?.Tune(tuneType);
        codeDb?.Tune(tuneType);
    }

    private async Task WaitForCloseToHead(CancellationToken token)
    {
        int totalSyncLag = syncConfig.StateMinDistanceFromHead + syncConfig.HeaderStateDistance;

        while (!token.IsCancellationRequested)
        {
            long header = syncProgressResolver.FindBestHeader();
            long peerBlock = syncPeerPool.InitializedPeers.Select(p => p.HeadNumber).DefaultIfEmpty(0).Max();
            long targetBlock = beaconSyncStrategy.GetTargetBlockHeight() ?? peerBlock;

            if (targetBlock >= header && (targetBlock - header) <= totalSyncLag)
                return;

            await Task.Delay(1000, token);
        }
    }
}
