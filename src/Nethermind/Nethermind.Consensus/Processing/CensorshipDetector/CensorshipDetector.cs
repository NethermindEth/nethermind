// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
    private readonly Dictionary<AddressAsKey, long> _poolAddressCensorshipTxCount = [];
    private readonly Dictionary<AddressAsKey, Transaction?> _poolAddressCensorshipBestTx = [];
    private readonly LruCache<BlockNumberHash, BlockCensorshipInfo> _potentiallyCensoredBlocks;
    private readonly WrapAroundArray<BlockNumberHash> _censoredBlocks;
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
                if (Address.TryParse(hexString, out Address address))
                {
                    _poolAddressCensorshipTxCount[address!] = 0;
                    _poolAddressCensorshipBestTx[address!] = null;
                }
            }
        }

        _potentiallyCensoredBlocks = new(_cacheSize, _cacheSize, "potentiallyCensoredBlocks");
        _censoredBlocks = new(_cacheSize);
        _txPool.NewPending += OnAddingTxToPool;
        _txPool.RemovedPending += OnRemovingTxFromPool;
        _txPool.EvictedPending += OnRemovingTxFromPool;
        _blockProcessor.BlockProcessing += OnBlockProcessing;
    }

    private void OnAddingTxToPool(object? sender, TxPool.TxEventArgs e)
    {
        Transaction tx = e.Transaction;
        if (tx.GasBottleneck > 0
        && tx.To is not null
        && _poolAddressCensorshipTxCount.TryGetValue(tx.To!, out long txCount)
        && _poolAddressCensorshipBestTx.TryGetValue(tx.To!, out Transaction? bestTx))
        {
            if (txCount == 0)
            {
                _poolAddressCensorshipBestTx[tx.To!] = tx;
            }
            _poolAddressCensorshipTxCount[tx.To!]++;
            if (_comparer.Compare(_poolAddressCensorshipBestTx[tx.To!], tx) > 0)
            {
                _poolAddressCensorshipBestTx[tx.To!] = tx;
            }
        }
    }

    private void OnRemovingTxFromPool(object? sender, TxPool.TxEventArgs e)
    {
        Transaction tx = e.Transaction;
        if (tx.GasBottleneck > 0
        && tx.To is not null
        && _poolAddressCensorshipTxCount.TryGetValue(tx.To!, out long txCount)
        && _poolAddressCensorshipBestTx.TryGetValue(tx.To!, out Transaction? bestTx))
        {
            if (_poolAddressCensorshipTxCount[tx.To!] == 1)
            {
                _poolAddressCensorshipBestTx[tx.To] = null;
            }
            _poolAddressCensorshipTxCount[tx.To!]--;
        }
    }

    private void OnBlockProcessing(object? sender, BlockEventArgs e)
    {
        Task.Run(() => Cache(e.Block));
    }

    private void Cache(Block block)
    {
        Transaction bestTxInBlock = block.Transactions[0];
        Transaction worstTxInBlock = block.Transactions[0];
        HashSet<AddressAsKey> blockAddressCensorshipDetectorHelper = [];
        // Number of unique addresses specified by the user for censorship detection, to which txs are sent in the block
        long blockCensorshipDetectionUniqueAddressCount = 0;

        foreach (Transaction tx in block.Transactions)
        {
            if (!tx.SupportsBlobs)
            {
                if (_poolAddressCensorshipTxCount.ContainsKey(tx.To!)
                && _poolAddressCensorshipBestTx.ContainsKey(tx.To!)
                && !blockAddressCensorshipDetectorHelper.Contains(tx.To!))
                {
                    blockAddressCensorshipDetectorHelper.Add(tx.To!);
                    blockCensorshipDetectionUniqueAddressCount++;
                }

                if (_comparer.Compare(bestTxInBlock, tx) > 0)
                {
                    bestTxInBlock = tx;
                }
                else
                {
                    worstTxInBlock = tx;
                }
            }
        }

        long poolCensorshipDetectionUniqueAddressCount = 0;
        foreach (Transaction? bestTx in _poolAddressCensorshipBestTx.Values)
        {
            if (bestTx is not null && _comparer.Compare(bestTx, worstTxInBlock) <= 0)
            {
                poolCensorshipDetectionUniqueAddressCount++;
            }
        }

        bool highPayingTxCensorship = _comparer.Compare(bestTxInBlock, _txPool.GetBestTx()) > 0;
        bool addressCensorship = blockCensorshipDetectionUniqueAddressCount * 2 < poolCensorshipDetectionUniqueAddressCount;
        bool isCensored = highPayingTxCensorship || addressCensorship;

        BlockCensorshipInfo blockCensorshipInfo = new(isCensored, block.ParentHash);
        _potentiallyCensoredBlocks.Set(new BlockNumberHash(block), blockCensorshipInfo);

        CensorshipDetection(block, isCensored);
    }

    public void CensorshipDetection(Block block, bool isCensored)
    {
        if (isCensored && block.Number > 2)
        {
            BlockCensorshipInfo b1 = _potentiallyCensoredBlocks.Get(new BlockNumberHash(block.Number - 1, (ValueHash256)block.ParentHash!));
            BlockCensorshipInfo b2 = _potentiallyCensoredBlocks.Get(new BlockNumberHash(block.Number - 2, (ValueHash256)b1.ParentHash!));
            BlockCensorshipInfo b3 = _potentiallyCensoredBlocks.Get(new BlockNumberHash(block.Number - 3, (ValueHash256)b2.ParentHash!));

            BlockNumberHash blockNumberHash = new(block);
            if (!_censoredBlocks.Contains(blockNumberHash) && b1.IsCensored && b2.IsCensored && b3.IsCensored)
            {
                _censoredBlocks.Add(blockNumberHash);
                Metrics.NumberOfCensoredBlocks++;
                Metrics.LastCensoredBlockNumber = block.Number;
                if (_logger.IsInfo)
                {
                    _logger.Info($"Censorship detected for block no. {block.Number} with hash {block.Hash!}");
                }
            }
        }
    }

    public IEnumerable<BlockNumberHash> GetCensoredBlocks() => _censoredBlocks.Items;

    public bool BlockPotentiallyCensored(long blockNumber, ValueHash256 blockHash) => _potentiallyCensoredBlocks.Contains(new BlockNumberHash(blockNumber, blockHash));

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
    public ValueHash256? ParentHash { get; }

    public BlockCensorshipInfo(bool isCensored, ValueHash256? parentHash)
    {
        IsCensored = isCensored;
        ParentHash = parentHash;
    }
}

public readonly struct BlockNumberHash : IEquatable<BlockNumberHash>
{
    public long Number { get; }
    public ValueHash256 Hash { get; }

    public BlockNumberHash(long number, ValueHash256 hash)
    {
        Number = number;
        Hash = hash;
    }

    public BlockNumberHash(Block block)
    {
        Number = block.Number;
        Hash = block.Hash ?? block.CalculateHash();
    }

    public bool Equals(BlockNumberHash other) => Number == other.Number && Hash == other.Hash;
}

public class WrapAroundArray<T>
{
    private readonly T[] _items = new T[1];
    private readonly long _maxSize = 1;
    private long _counter = 0;

    public WrapAroundArray(long maxSize)
    {
        if (maxSize > 1)
        {
            _maxSize = maxSize;
            _items = new T[maxSize];
        }
    }

    public void Add(T item)
    {
        _items[(int)(_counter % _maxSize)] = item;
        _counter++;
    }

    public bool Contains(T item) => _items.Contains(item);

    public IEnumerable<T> Items => _items;
}
