// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Blockchain.HistoryPruning;

public class HistoryPruner(
    IBlockTree blockTree,
    IReceiptStorage receiptStorage,
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
    private readonly ulong _minHistoryRetentionEpochs = 82125;

    public class HistoryPrunerException(string message) : Exception(message)
    {
    }

    public async Task TryPruneHistory(CancellationToken cancellationToken)
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

            _pruneHistoryTask = PruneHistory(cancellationToken);
        }
        await _pruneHistoryTask;
    }

    public void CheckConfig()
    {
        if (historyConfig.HistoryRetentionEpochs < _minHistoryRetentionEpochs)
        {
            throw new HistoryPrunerException($"HistoryRetentionEpochs must be at least {_minHistoryRetentionEpochs}.");
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

    private async Task PruneHistory(CancellationToken cancellationToken)
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

        await Task.Run(() =>
        {
            var deletedBlocks = blockTree.DeleteBlocksBeforeTimestamp(cutoffTimestamp, cancellationToken);
            PruneReceipts(deletedBlocks);
        }, cancellationToken);

        _lastPrunedTimestamp = cutoffTimestamp;
        if (_logger.IsInfo) _logger.Info($"Pruned historical blocks up to timestamp {cutoffTimestamp}");
    }

    private void PruneReceipts(IEnumerable<Block> blocks)
    {
        foreach (Block block in blocks)
        {
            receiptStorage.RemoveReceipts(block);
        }
    }

    private ulong CalculateCutoffTimestamp()
    {
        ulong cutoffTimestamp = 0;

        if (historyConfig.HistoryRetentionEpochs.HasValue)
        {
            cutoffTimestamp = blockTree.Head!.Timestamp - (historyConfig.HistoryRetentionEpochs.Value * _epochLength);
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
