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
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;

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
    private readonly IBlockStore _blockStore;
    private readonly IChainLevelInfoRepository _chainLevelInfoRepository;
    private readonly IDb _metadataDb;
    private readonly IHistoryConfig _historyConfig;
    private readonly bool _enabled;
    private readonly long _epochLength;
    private readonly long _minHistoryRetentionEpochs;
    private long _deletePointer = 1;

    public class HistoryPrunerException(string message, Exception? innerException = null) : Exception(message, innerException);

    public HistoryPruner(
        IBlockTree blockTree,
        IReceiptStorage receiptStorage,
        ISpecProvider specProvider,
        IBlockStore blockStore,
        IChainLevelInfoRepository chainLevelInfoRepository,
        IDb metadataDb,
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
        _metadataDb = metadataDb;
        _historyConfig = historyConfig;
        _enabled = historyConfig.Enabled;
        _epochLength = secondsPerSlot * 32;
        _minHistoryRetentionEpochs = specProvider.GenesisSpec.MinHistoryRetentionEpochs;

        CheckConfig();
        LoadDeletePointer();
    }

    public void OnBlockProcessorQueueEmpty(object? sender, EventArgs e)
        => _ = TryPruneHistory(CancellationToken.None);

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

    public long? CutoffBlockNumber
    {
        get
        {
            ulong cutoffTimestamp = CalculateCutoffTimestamp();

            if (_lastCutoffTimestamp is not null && cutoffTimestamp == _lastCutoffTimestamp)
            {
                return _lastCutoffBlockNumber;
            }

            long? cutoffBlockNumber = null;
            GetBlocksByNumber(_deletePointer, b =>
            {
                // end search when we find a block after the cutoff
                bool afterCutoff = b.Timestamp >= cutoffTimestamp;
                if (afterCutoff)
                {
                    cutoffBlockNumber = b.Number;
                }
                return afterCutoff;
            }, _ => { });

            _lastCutoffTimestamp = cutoffTimestamp;
            _lastCutoffBlockNumber = cutoffBlockNumber;

            return cutoffBlockNumber;
        }
    }

    private ulong? _lastCutoffTimestamp;
    private long? _lastCutoffBlockNumber;

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

        await Task.Run(() => PruneBlocksAndReceipts(cutoffTimestamp, cancellationToken), cancellationToken);

        _lastPrunedTimestamp = cutoffTimestamp;
        if (_logger.IsInfo) _logger.Info($"Pruned historical blocks up to timestamp {cutoffTimestamp}");
    }

    private void PruneBlocksAndReceipts(ulong cutoffTimestamp, CancellationToken cancellationToken)
    {
        int deletedBlocks = 0;
        try
        {
            using BatchWrite batch = _chainLevelInfoRepository.StartBatch();

            IEnumerable<Block> blocks = GetBlocksBeforeTimestamp(cutoffTimestamp);
            foreach (Block block in blocks)
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

                // todo: change to debug after testing
                if (_logger.IsInfo) _logger.Info($"Deleting old block {number} with hash {hash}.");
                _blockTree.DeleteBlock(number, hash, null, batch, null, true);
                _receiptStorage.RemoveReceipts(block);
                _deletePointer = number;
                deletedBlocks++;
            }
        }
        finally
        {
            SaveDeletePointer();
            if (_logger.IsInfo) _logger.Info($"Completed pruning operation up to timestamp {cutoffTimestamp}. Deleted {deletedBlocks} blocks up to #{_deletePointer}.");
        }
    }

    private IEnumerable<Block> GetBlocksBeforeTimestamp(ulong cutoffTimestamp)
        => GetBlocksByNumber(_deletePointer, b => b.Timestamp >= cutoffTimestamp, i => _deletePointer = i);

    private IEnumerable<Block> GetBlocksByNumber(long from, Predicate<Block> endSearch, Action<long> onFirstBlock)
    {
        bool firstBlock = true;
        long headNumber = _blockTree.Head!.Number;
        for (long i = from; i <= headNumber; i++)
        {
            ChainLevelInfo? chainLevelInfo = _chainLevelInfoRepository.LoadLevel(i);
            if (chainLevelInfo is null)
            {
                continue;
            }
            else if (firstBlock)
            {
                onFirstBlock(i);
                firstBlock = false;
            }

            bool finished = false;
            foreach (BlockInfo blockInfo in chainLevelInfo.BlockInfos)
            {
                Block? block = _blockStore.Get(blockInfo.BlockNumber, blockInfo.BlockHash);
                if (block is null)
                {
                    continue;
                }

                // search entire chain level before finishing
                if (endSearch(block))
                {
                    finished = true;
                }
                else
                {
                    yield return block;
                }
            }

            if (finished)
            {
                break;
            }
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

    private void LoadDeletePointer()
    {
        byte[]? val = _metadataDb.Get(MetadataDbKeys.HistoryPruningDeletePointer);
        if (val is not null)
        {
            _deletePointer = val.AsRlpStream().DecodeLong();
        }
    }

    private void SaveDeletePointer()
        => _metadataDb.Set(MetadataDbKeys.HistoryPruningDeletePointer, Rlp.Encode(_deletePointer).Bytes);
}
