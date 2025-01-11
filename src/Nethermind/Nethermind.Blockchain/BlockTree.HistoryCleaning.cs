// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain;

public partial class BlockTree
{
    private Task? _pruneHistoryTask;
    private readonly Lock _pruneLock = new();

    public void TryPruneHistory()
    {
        if (!ShouldPruneHistory())
        {
            if (_logger.IsInfo) _logger.Info("Historical pruning is not enabled");
            return;
        }

        if (_logger.IsInfo) _logger.Info("Historical pruning is enabled");

        lock (_pruneLock)
        {
            if (_pruneHistoryTask is not null && !_pruneHistoryTask.IsCompleted)
            {
                return;
            }

            _pruneHistoryTask = Task.Run(ExecuteHistoryPruning);
        }
    }

    private bool ShouldPruneHistory()
        => _historyConfig.HistoryPruneEpochs is not null || _historyConfig.DropPreMerge;

    private void ExecuteHistoryPruning()
    {
        if (Head is null)
        {
            return;
        }

        ulong cutoffTimestamp = CalculateCutoffTimestamp();

        if (_logger.IsInfo) _logger.Info($"Pruning historical blocks up to timestamp {cutoffTimestamp}");

        DeleteBlocksBeforeTimestamp(cutoffTimestamp);

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
        try
        {
            using (_chainLevelInfoRepository.StartBatch())
            {
                foreach ((long _, Hash256 blockHash) in _blockStore.GetBlocksOlderThan(cutoffTimestamp))
                {
                    DeleteBlocks(blockHash);
                }
            }
        }
        finally
        {
            ReleaseAcceptingNewBlocks();
        }
    }
}