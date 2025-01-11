// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain;

public partial class BlockTree
{
    private Task? _pruneHistoryTask;
    private readonly Lock _pruneLock = new();
    private ulong _lastPrunedTimestamp;

    public void TryPruneHistory()
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

            _pruneHistoryTask = ExecuteHistoryPruningAsync();
        }
    }

    private bool ShouldPruneHistory()
    {
        if (!(_historyConfig.HistoryPruneEpochs is not null || _historyConfig.DropPreMerge))
        {
            return false;
        }

        ulong cutoffTimestamp = CalculateCutoffTimestamp();
        return cutoffTimestamp > _lastPrunedTimestamp;
    }

    private async Task ExecuteHistoryPruningAsync()
    {
        if (Head is null)
        {
            return;
        }

        ulong cutoffTimestamp = CalculateCutoffTimestamp();
        
        if (cutoffTimestamp <= _lastPrunedTimestamp)
        {
            return;
        }

        if (_logger.IsInfo) _logger.Info($"Pruning historical blocks up to timestamp {cutoffTimestamp}");

        await Task.Run(() => DeleteBlocksBeforeTimestamp(cutoffTimestamp));

        _lastPrunedTimestamp = cutoffTimestamp;
        if (_logger.IsInfo) _logger.Info($"Pruned historical blocks up to timestamp {cutoffTimestamp}");
    }

    private ulong CalculateCutoffTimestamp()
    {
        const ulong SLOTS_PER_EPOCH = 32;
        ulong epochLength = _blocksConfig.SecondsPerSlot * SLOTS_PER_EPOCH;
        ulong cutoffTimestamp = 0;

        if (_historyConfig.HistoryPruneEpochs.HasValue)
        {
            cutoffTimestamp = Head!.Timestamp - (_historyConfig.HistoryPruneEpochs.Value * epochLength);
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

    private void DeleteBlocksBeforeTimestamp(ulong cutoffTimestamp)
    {
        BlockAcceptingNewBlocks();
        int deletedBlocks = 0;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(_historyConfig.PruningTimeout));
            using (_chainLevelInfoRepository.StartBatch())
            {
                foreach ((long _, Hash256 blockHash) in _blockStore.GetBlocksOlderThan(cutoffTimestamp, _logger))
                {
                    if (cts.Token.IsCancellationRequested)
                    {
                        if (_logger.IsInfo) _logger.Info($"Pruning operation timed out at timestamp {cutoffTimestamp}. Deleted {deletedBlocks} blocks.");
                        break;
                    }

                    DeleteBlocks(blockHash);
                    deletedBlocks++;
                }
            }
        }
        finally
        {
            if (_logger.IsInfo) _logger.Info($"Completed pruning operation up to timestamp {cutoffTimestamp}. Deleted {deletedBlocks} blocks.");
            ReleaseAcceptingNewBlocks();
        }
    }
}
