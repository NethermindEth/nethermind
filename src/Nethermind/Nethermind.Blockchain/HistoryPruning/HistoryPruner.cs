// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Blockchain.HistoryPruning;

public class HistoryPruner(
    IBlockTree blockTree,
    ISpecProvider specProvider,
    IHistoryConfig historyConfig,
    ulong secondsPerSlot,
    ILogManager logManager) : IHistoryPruner
{
    private Task? _pruneHistoryTask;
    private readonly Lock _pruneLock = new();
    private ulong _lastPrunedTimestamp;
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly bool _enabled = historyConfig.Enabled;
    private readonly ulong _epochLength = secondsPerSlot * 32;

    public void TryPruneHistory(CancellationToken cancellationToken)
    {
        if (!ShouldPruneHistory())
        {
            return;
        }

        lock (_pruneLock)
        {
            if (_pruneHistoryTask is not null && !_pruneHistoryTask.IsCompleted)
            {
                return;
            }

            _pruneHistoryTask = ExecuteHistoryPruningAsync(cancellationToken);
        }
    }

    private bool ShouldPruneHistory()
    {
        if (!_enabled)
        {
            return false;
        }

        ulong cutoffTimestamp = CalculateCutoffTimestamp();
        return cutoffTimestamp > _lastPrunedTimestamp;
    }

    private async Task ExecuteHistoryPruningAsync(CancellationToken cancellationToken)
    {
        if (blockTree.Head is null)
        {
            return;
        }

        ulong cutoffTimestamp = CalculateCutoffTimestamp();

        if (cutoffTimestamp <= _lastPrunedTimestamp)
        {
            return;
        }

        if (_logger.IsInfo) _logger.Info($"Pruning historical blocks up to timestamp {cutoffTimestamp}");

        await Task.Run(() => blockTree.DeleteBlocksBeforeTimestamp(cutoffTimestamp, cancellationToken), cancellationToken);

        _lastPrunedTimestamp = cutoffTimestamp;
        if (_logger.IsInfo) _logger.Info($"Pruned historical blocks up to timestamp {cutoffTimestamp}");
    }

    private ulong CalculateCutoffTimestamp()
    {
        ulong cutoffTimestamp = 0;

        if (historyConfig.HistoryPruneEpochs.HasValue)
        {
            cutoffTimestamp = blockTree.Head!.Timestamp - (historyConfig.HistoryPruneEpochs.Value * _epochLength);
        }

        if (historyConfig.DropPreMerge)
        {
            ulong? beaconGenesisTimestamp = specProvider.BeaconChainGenesisTimestamp;
            if (beaconGenesisTimestamp.HasValue && beaconGenesisTimestamp.Value > cutoffTimestamp)
            {
                cutoffTimestamp = beaconGenesisTimestamp.Value;
            }
        }

        return cutoffTimestamp;
    }
}
