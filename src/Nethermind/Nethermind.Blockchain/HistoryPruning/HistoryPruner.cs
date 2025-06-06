// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Blockchain.HistoryPruning;

public class HistoryPruner : IHistoryPruner
{
    private Task? _pruneHistoryTask;
    private readonly Lock _pruneLock = new();
    private ulong _lastPrunedTimestamp;
    private readonly ISpecProvider _specProvider;
    private readonly ILogger _logger;
    private readonly IBlockTree _blockTree;
    private readonly IReceiptStorage _receiptStorage;
    private readonly IHistoryConfig _historyConfig;
    private readonly bool _enabled;
    private readonly long _epochLength;
    private readonly long _minHistoryRetentionEpochs;

    public HistoryPruner(
        IBlockTree blockTree,
        IReceiptStorage receiptStorage,
        ISpecProvider specProvider,
        IHistoryConfig historyConfig,
        long secondsPerSlot,
        ILogManager logManager)
    {
        _specProvider = specProvider;
        _logger = logManager.GetClassLogger();
        _blockTree = blockTree;
        _receiptStorage = receiptStorage;
        _historyConfig = historyConfig;
        _enabled = historyConfig.Enabled;
        _epochLength = secondsPerSlot * 32;
        _minHistoryRetentionEpochs = specProvider.GetFinalSpec().MinHistoryRetentionEpochs;
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

    public bool CheckConfig()
    {
        if (_historyConfig.HistoryRetentionEpochs is not null &&
            _historyConfig.HistoryRetentionEpochs < _minHistoryRetentionEpochs)
        {
            _logger.Error($"HistoryRetentionEpochs must be at least {_minHistoryRetentionEpochs}.");
            return false;
        }
        return true;
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
        if (_blockTree.Head is null)
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
            var deletedBlocks = _blockTree.DeleteBlocksBeforeTimestamp(cutoffTimestamp, cancellationToken);
            PruneReceipts(deletedBlocks);
        }, cancellationToken);

        _lastPrunedTimestamp = cutoffTimestamp;
        if (_logger.IsInfo) _logger.Info($"Pruned historical blocks up to timestamp {cutoffTimestamp}");
    }

    private void PruneReceipts(IEnumerable<Block> blocks)
    {
        foreach (Block block in blocks)
        {
            _receiptStorage.RemoveReceipts(block);
        }
    }

    private ulong CalculateCutoffTimestamp()
    {
        ulong cutoffTimestamp = 0;

        if (_historyConfig.HistoryRetentionEpochs.HasValue)
        {
            cutoffTimestamp = _blockTree.Head!.Timestamp - (ulong)(_historyConfig.HistoryRetentionEpochs.Value * _epochLength);
        }

        if (_historyConfig.DropPreMerge)
        {
            ulong? beaconGenesisTimestamp = _specProvider.BeaconChainGenesisTimestamp;
            if (beaconGenesisTimestamp.HasValue && beaconGenesisTimestamp.Value > cutoffTimestamp)
            {
                cutoffTimestamp = beaconGenesisTimestamp.Value;
            }
        }

        return cutoffTimestamp;
    }
}
