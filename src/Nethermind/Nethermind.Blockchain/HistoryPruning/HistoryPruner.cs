// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State.Repositories;

namespace Nethermind.Blockchain.HistoryPruning;

public class HistoryPruner : IHistoryPruner
{
    private Task? _pruneHistoryTask;
    private readonly Lock _pruneLock = new();
    private ulong _lastPrunedTimestamp;
    private readonly ISpecProvider _specProvider;
    private readonly ILogger _logger;
    private readonly IBlockTree _blockTree ;
    private readonly IReceiptStorage _receiptStorage;
    private readonly IBlockStore _blockStore;
    private readonly IChainLevelInfoRepository _chainLevelInfoRepository;
    private readonly IHistoryConfig _historyConfig;
    private readonly bool _enabled;
    private readonly long _epochLength;
    private readonly long _minHistoryRetentionEpochs;

    public class HistoryPrunerException(string message, Exception? innerException = null) : Exception(message, innerException);

    public HistoryPruner(
        IBlockTree blockTree,
        IReceiptStorage receiptStorage,
        ISpecProvider specProvider,
        IBlockStore blockStore,
        IChainLevelInfoRepository chainLevelInfoRepository,
        IHistoryConfig historyConfig,
        long secondsPerSlot,
        ILogManager logManager)
    {
        _specProvider = specProvider;
        _logger = logManager.GetClassLogger();
        _blockTree = blockTree;
        _receiptStorage = receiptStorage;
        _blockStore = blockStore;
        _chainLevelInfoRepository = chainLevelInfoRepository;
        _historyConfig = historyConfig;
        _enabled = historyConfig.Enabled;
        _epochLength = secondsPerSlot * 32;
        _minHistoryRetentionEpochs = specProvider.GenesisSpec.MinHistoryRetentionEpochs;

        CheckConfig();
    }

    public void OnBlockProcessorQueueEmpty(object? sender, EventArgs e)
    {
        _ = TryPruneHistory(CancellationToken.None);
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

    private void CheckConfig()
    {
        if (_historyConfig.HistoryRetentionEpochs is not null &&
            _historyConfig.HistoryRetentionEpochs < _minHistoryRetentionEpochs)
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
            IEnumerable<Block> deletedBlocks = DeleteBlocksBeforeTimestamp(cutoffTimestamp, cancellationToken);
            PruneReceipts(deletedBlocks);
        }, cancellationToken);

        _lastPrunedTimestamp = cutoffTimestamp;
        if (_logger.IsInfo) _logger.Info($"Pruned historical blocks up to timestamp {cutoffTimestamp}");
    }

    public IEnumerable<Block> DeleteBlocksBeforeTimestamp(ulong cutoffTimestamp, CancellationToken cancellationToken)
    {
        int deletedBlocks = 0;
        try
        {
            using BatchWrite batch = _chainLevelInfoRepository.StartBatch();

            IEnumerable<Block> oldBlocks = _blockStore.GetBlocksOlderThan(cutoffTimestamp);
            foreach (Block block in oldBlocks)
            {
                long number = block.Number;
                Hash256 hash = block.Hash;

                if (cancellationToken.IsCancellationRequested)
                {
                    if (_logger.IsInfo) _logger.Info($"Pruning operation timed out at timestamp {cutoffTimestamp}. Deleted {deletedBlocks} blocks.");
                    break;
                }

                if (number == 0 || number >= _blockTree.SyncPivot.BlockNumber)
                {
                    continue;
                }

                if (_logger.IsInfo) _logger.Info($"Deleting old block {number} with hash {hash}.");
                _blockTree.DeleteBlock(number, hash, null, batch, null, true);
                deletedBlocks++;
                yield return block;
            }
        }
        finally
        {
            if (_logger.IsInfo) _logger.Info($"Completed pruning operation up to timestamp {cutoffTimestamp}. Deleted {deletedBlocks} blocks.");
        }
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
