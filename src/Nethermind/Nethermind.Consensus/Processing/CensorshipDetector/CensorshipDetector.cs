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
using Nethermind.Int256;
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
                    _poolAddressCensorshipBestTx[address!] = null;
                }
            }
        }

        _potentiallyCensoredBlocks = new(_cacheSize, _cacheSize, "potentiallyCensoredBlocks");
        _censoredBlocks = new(_cacheSize);
        _blockProcessor.BlockProcessing += OnBlockProcessing;
    }

    private void OnBlockProcessing(object? sender, BlockEventArgs e)
    {
        if (_poolAddressCensorshipBestTx.Count != 0)
        {
            UInt256 baseFee = e.Block.BaseFeePerGas;
            IEnumerable<Transaction> poolBestTransactions = _txPool.GetBestTxOfEachSender();
            foreach (Transaction tx in poolBestTransactions)
            {
                // checking tx.GasBottleneck > baseFee ensures only ready transactions are considered.
                if (tx.To is not null && tx.GasBottleneck > baseFee && _poolAddressCensorshipBestTx.TryGetValue(tx.To!, out Transaction? bestTx))
                {
                    if (bestTx is null)
                    {
                        _poolAddressCensorshipBestTx[tx.To!] = tx;
                    }
                    else if (_comparer.Compare(bestTx, tx) > 0)
                    {
                        _poolAddressCensorshipBestTx[tx.To!] = tx;
                    }
                }
            }
            Task.Run(() => Cache(e.Block, true));
        }
        else
        {
            Task.Run(() => Cache(e.Block, false));
        }
    }

    private void Cache(Block block, bool detectingAddressCensorship)
    {
        // Number of unique addresses specified by the user for censorship detection, to which txs are sent in the block.
        long blockCensorshipDetectionUniqueAddressCount = 0;
        /* 
         * Number of unique addresses specified by the user for censorship detection, to which includable txs are sent in the pool.
         * Includable txs comprise of pool transactions better than the worst tx in block.
         */
        long poolCensorshipDetectionUniqueAddressCount = 0;
        Transaction bestTxInBlock = block.Transactions[0];
        Transaction worstTxInBlock = block.Transactions[0];
        HashSet<AddressAsKey> blockAddressCensorshipDetectorHelper = [];

        /* 
         * Iterates through the block's transactions to get the best tx in block, used in detecting default high-paying tx censorship.
         * If detectingAddressCensorship is marked true, we get the worst tx in block to determine includable txs as well as
           getting the blockCensorshipDetectionUniqueAddressCount, used in detecting the optional address censorship.
         */
        foreach (Transaction tx in block.Transactions)
        {
            if (!tx.SupportsBlobs)
            {
                if (_comparer.Compare(bestTxInBlock, tx) > 0)
                {
                    bestTxInBlock = tx;
                }
                if (detectingAddressCensorship)
                {
                    if (_comparer.Compare(worstTxInBlock, tx) < 0)
                    {
                        worstTxInBlock = tx;
                    }
                    if (_poolAddressCensorshipBestTx.ContainsKey(tx.To!) && !blockAddressCensorshipDetectorHelper.Contains(tx.To!))
                    {
                        blockAddressCensorshipDetectorHelper.Add(tx.To!);
                        blockCensorshipDetectionUniqueAddressCount++;
                    }
                }
            }
        }

        foreach (Transaction? bestTx in _poolAddressCensorshipBestTx.Values)
        {
            if (bestTx is not null && _comparer.Compare(bestTx, worstTxInBlock) < 0)
            {
                poolCensorshipDetectionUniqueAddressCount++;
            }
        }

        /* 
         * Checking to see if the block exhibits high-paying tx censorship or address censorship or both.
         * High-paying tx censorship is flagged if the best tx in the pool is not included in the block.
         * Address censorship is flagged if txs sent to less than half of the user-specified addresses 
           for censorship detection with includable txs in the pool are included in the block.
         */
        bool isCensored = _comparer.Compare(bestTxInBlock, _txPool.GetBestTx()) > 0
        || blockCensorshipDetectionUniqueAddressCount * 2 < poolCensorshipDetectionUniqueAddressCount;

        BlockCensorshipInfo blockCensorshipInfo = new(isCensored, block.ParentHash);
        _potentiallyCensoredBlocks.Set(new BlockNumberHash(block), blockCensorshipInfo);

        CensorshipDetection(block, isCensored);

        ClearAddressCensorshipDetectionDictionary();
    }

    public void CensorshipDetection(Block block, bool isCensored)
    {
        // Censorship is detected if potential censorship is flagged for the last 4 blocks including the latest.
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

    public void ClearAddressCensorshipDetectionDictionary()
    {
        foreach (AddressAsKey key in _poolAddressCensorshipBestTx.Keys)
        {
            _poolAddressCensorshipBestTx[key] = null;
        }
    }

    public void Dispose()
    {
        _blockProcessor.BlockProcessing -= OnBlockProcessing;
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
