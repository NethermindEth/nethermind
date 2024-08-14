// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Processing.CensorshipDetector;

public class CensorshipDetector : IDisposable
{
    private readonly ITxPool _txPool;
    private readonly IComparer<Transaction> _comparer;
    private readonly IBlockProcessor _blockProcessor;
    private readonly ILogger _logger;
    private readonly ICensorshipDetectorConfig _censorshipDetectorConfig;
    private readonly SortedDictionary<Address, long> _poolAddressCensorshipDetectorHelper = [];
    private readonly LruCache<BlockNumberHash, BlockCensorshipInfo> _potentiallyCensoredBlocks;
    private IEnumerable<BlockNumberHash> _censoredBlocks = [];
    private const int _cacheSize = 64;

    public CensorshipDetector(
        ITxPool txPool,
        IComparer<Transaction> comparer,
        IBlockProcessor blockProcessor,
        ILogManager logManager,
        ICensorshipDetectorConfig censorshipDetectorConfig)
    {
        _txPool = txPool;
        _comparer = comparer;
        _blockProcessor = blockProcessor;
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        _censorshipDetectorConfig = censorshipDetectorConfig;

        if (_censorshipDetectorConfig.AddressesForCensorshipDetection is not null)
        {
            foreach (string hexString in _censorshipDetectorConfig.AddressesForCensorshipDetection)
            {
                if (Address.IsValidAddress(hexString, true) && Address.TryParse(hexString, out Address address))
                {
                    _poolAddressCensorshipDetectorHelper.Add(address!, 0);
                }
            }
        }

        _potentiallyCensoredBlocks = new(_cacheSize, _cacheSize, "potentiallyCensoredBlocks");
        _txPool.NewPending += OnAddingTxToPool;
        _txPool.RemovedPending += OnRemovingTxFromPool;
        _txPool.EvictedPending += OnRemovingTxFromPool;
        _blockProcessor.BlockProcessing += OnBlockProcessing;
    }

    private void OnAddingTxToPool(object? sender, TxPool.TxEventArgs e)
    {
        Transaction tx = e.Transaction;
        if (tx.To is not null && _poolAddressCensorshipDetectorHelper.TryGetValue(tx.To!, out long txSentToAddressCount))
        {
            if (txSentToAddressCount == 0)
            {
                Metrics.PoolCensorshipDetectionUniqueAddressesCount++;
            }
            _poolAddressCensorshipDetectorHelper[tx.To!]++;
        }
    }

    private void OnRemovingTxFromPool(object? sender, TxPool.TxEventArgs e)
    {
        Transaction tx = e.Transaction;
        if (tx.To is not null && _poolAddressCensorshipDetectorHelper.TryGetValue(tx.To!, out long txSentToAddressCount) && txSentToAddressCount > 0)
        {
            if (txSentToAddressCount == 1)
            {
                Metrics.PoolCensorshipDetectionUniqueAddressesCount--;
            }
            _poolAddressCensorshipDetectorHelper[tx.To!]--;
        }
    }

    private void OnBlockProcessing(object? sender, BlockEventArgs e)
    {
        Task.Run(() => Cache(e.Block));
    }

    private void Cache(Block block)
    {
        Transaction bestTxInPool = _txPool.GetBestTx();
        long _poolCensorshipDetectionUniqueAddressCount = Metrics.PoolCensorshipDetectionUniqueAddressesCount;

        Transaction bestTxInBlock = block.Transactions[0];
        SortedDictionary<Address, bool> _blockAddressCensorshipDetectorHelper = [];
        // Number of unique addresses specified by the user for censorship detection, to which txs are sent in the block
        long _blockCensorshipDetectionUniqueAddressCount = 0;

        foreach (Transaction tx in block.Transactions)
        {
            if (!tx.SupportsBlobs)
            {
                if (_poolAddressCensorshipDetectorHelper.TryGetValue(tx.To!, out _) && !_blockAddressCensorshipDetectorHelper.TryGetValue(tx.To!, out _))
                {
                    _blockAddressCensorshipDetectorHelper[tx.To!] = true;
                    _blockCensorshipDetectionUniqueAddressCount++;
                }

                if (_comparer.Compare(bestTxInBlock, tx) > 0)
                {
                    bestTxInBlock = tx;
                }
            }
        }

        bool _highPayingTxCensorship = _comparer.Compare(bestTxInBlock, bestTxInPool) > 0;
        bool _addressCensorship = _blockCensorshipDetectionUniqueAddressCount * 2 < _poolCensorshipDetectionUniqueAddressCount;

        BlockNumberHash blockNumberHash = new(block);
        BlockCensorshipInfo blockCensorshipInfo = new(_highPayingTxCensorship || _addressCensorship, block.ParentHash);
        _potentiallyCensoredBlocks.Set(blockNumberHash, blockCensorshipInfo);

        // Checking last 3 blocks for potential censorship
        if (blockCensorshipInfo.IsCensored && block.Number > 2)
        {
            BlockCensorshipInfo b1 = _potentiallyCensoredBlocks.Get(new BlockNumberHash(block.Number - 1, blockCensorshipInfo.ParentHash!));
            BlockCensorshipInfo b2 = _potentiallyCensoredBlocks.Get(new BlockNumberHash(block.Number - 2, b1.ParentHash!));
            BlockCensorshipInfo b3 = _potentiallyCensoredBlocks.Get(new BlockNumberHash(block.Number - 3, b2.ParentHash!));

            if (!_censoredBlocks.Contains(blockNumberHash) && b1.IsCensored && b2.IsCensored && b3.IsCensored)
            {
                _censoredBlocks = _censoredBlocks.Append(blockNumberHash);
                Metrics.NumberOfCensoredBlocks++;
                Metrics.LastCensoredBlockNumber = block.Number;
                if (_logger.IsInfo)
                {
                    _logger.Info($"Censorship detected for block no. {block.Number} with hash {block.Hash!}");
                }
            }
        }
    }

    public IEnumerable<BlockNumberHash> GetCensoredBlocks() => _censoredBlocks;

    public bool BlockPotentiallyCensored(long blockNumber, Hash256 blockHash) => _potentiallyCensoredBlocks.Contains(new BlockNumberHash(blockNumber, blockHash));

    public void Dispose()
    {
        _blockProcessor.BlockProcessing -= OnBlockProcessing;
        _txPool.NewPending -= OnAddingTxToPool;
        _txPool.RemovedPending -= OnRemovingTxFromPool;
        _txPool.EvictedPending -= OnRemovingTxFromPool;
    }
}

public readonly struct BlockCensorshipInfo
{
    public bool IsCensored { get; }
    public Hash256? ParentHash { get; }

    public BlockCensorshipInfo(bool isCensored, Hash256? parentHash)
    {
        IsCensored = isCensored;
        ParentHash = parentHash;
    }
}

public readonly struct BlockNumberHash
{
    public long Number { get; }
    public Hash256 Hash { get; }

    public BlockNumberHash(long number, Hash256 hash)
    {
        Number = number;
        Hash = hash;
    }

    public BlockNumberHash(Block block)
    {
        Number = block.Number;
        Hash = block.Hash ?? block.CalculateHash();
    }
}
