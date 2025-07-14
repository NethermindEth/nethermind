// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Processing.CensorshipDetector;

public interface ICensorshipDetector
{
    IEnumerable<BlockNumberHash> GetCensoredBlocks();
    bool BlockPotentiallyCensored(long blockNumber, ValueHash256 blockHash);
}

public class NoopCensorshipDetector : ICensorshipDetector
{
    public IEnumerable<BlockNumberHash> GetCensoredBlocks()
    {
        return [];
    }

    public bool BlockPotentiallyCensored(long blockNumber, ValueHash256 blockHash)
    {
        return false;
    }
}

public class CensorshipDetector : IDisposable, ICensorshipDetector
{
    private readonly IBlockTree _blockTree;
    private readonly ITxPool _txPool;
    private readonly IComparer<Transaction> _betterTxComparer;
    private readonly IBranchProcessor _blockProcessor;
    private readonly ILogger _logger;
    private readonly Dictionary<AddressAsKey, Transaction?>? _bestTxPerObservedAddresses;
    private readonly LruCache<BlockNumberHash, BlockCensorshipInfo> _potentiallyCensoredBlocks;
    private readonly WrapAroundArray<BlockNumberHash> _censoredBlocks;
    private readonly uint _blockCensorshipThreshold;
    private readonly int _cacheSize;

    public CensorshipDetector(
        IBlockTree blockTree,
        ITxPool txPool,
        IComparer<Transaction> betterTxComparer,
        IBranchProcessor blockProcessor,
        ILogManager logManager,
        ICensorshipDetectorConfig censorshipDetectorConfig)
    {
        _blockTree = blockTree;
        _txPool = txPool;
        _betterTxComparer = betterTxComparer;
        _blockProcessor = blockProcessor;
        _blockCensorshipThreshold = censorshipDetectorConfig.BlockCensorshipThreshold;
        _cacheSize = (int)(4 * _blockCensorshipThreshold);
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

        if (censorshipDetectorConfig.AddressesForCensorshipDetection is not null)
        {
            foreach (string hexString in censorshipDetectorConfig.AddressesForCensorshipDetection)
            {
                if (Address.TryParse(hexString, out Address address))
                {
                    _bestTxPerObservedAddresses ??= new Dictionary<AddressAsKey, Transaction>();
                    _bestTxPerObservedAddresses[address!] = null;
                }
                else
                {
                    if (_logger.IsWarn) _logger.Warn($"Invalid address {hexString} provided for censorship detection.");
                }
            }
        }

        _potentiallyCensoredBlocks = new(_cacheSize, _cacheSize, "potentiallyCensoredBlocks");
        _censoredBlocks = new(_cacheSize);
        _blockProcessor.BlockProcessing += OnBlockProcessing;
    }

    private bool IsSyncing()
    {
        (bool isSyncing, _, _) = _blockTree.IsSyncing();
        return isSyncing;
    }

    private void OnBlockProcessing(object? sender, BlockEventArgs e)
    {
        // skip censorship detection if node is not synced yet
        if (IsSyncing()) return;

        bool tracksPerAddressCensorship = _bestTxPerObservedAddresses is not null;
        if (tracksPerAddressCensorship)
        {
            UInt256 baseFee = e.Block.BaseFeePerGas;
            IEnumerable<Transaction> poolBestTransactions = _txPool.GetBestTxOfEachSender();
            foreach (Transaction tx in poolBestTransactions)
            {
                // checking tx.GasBottleneck > baseFee ensures only ready transactions are considered.
                if (tx.To is not null
                    && tx.GasBottleneck > baseFee
                    && _bestTxPerObservedAddresses.TryGetValue(tx.To, out Transaction? bestTx)
                    && (bestTx is null || _betterTxComparer.Compare(bestTx, tx) > 0))
                {
                    _bestTxPerObservedAddresses[tx.To] = tx;
                }
            }
        }

        Task.Run(() => Cache(e.Block));
    }

    private void Cache(Block block)
    {
        bool tracksPerAddressCensorship = _bestTxPerObservedAddresses is not null;

        try
        {
            if (block.Transactions.Length == 0)
            {
                BlockCensorshipInfo blockCensorshipInfo = new(false, block.ParentHash);
                BlockNumberHash blockNumberHash = new BlockNumberHash(block);
                _potentiallyCensoredBlocks.Set(blockNumberHash, blockCensorshipInfo);
            }
            else
            {
                // Number of unique addresses specified by the user for censorship detection, to which txs are sent in the block.
                long blockTxsOfTrackedAddresses = 0;

                // Number of unique addresses specified by the user for censorship detection, to which includable txs are sent in the pool.
                // Includable txs consist of pool transactions better than the worst tx in block.
                long poolTxsThatAreBetterThanWorstInBlock = 0;

                Transaction bestTxInBlock = block.Transactions[0];
                Transaction worstTxInBlock = block.Transactions[0];
                HashSet<AddressAsKey> trackedAddressesInBlock = [];

                foreach (Transaction tx in block.Transactions)
                {
                    if (!tx.SupportsBlobs)
                    {
                        // Finds best tx in block
                        if (_betterTxComparer.Compare(bestTxInBlock, tx) > 0)
                        {
                            bestTxInBlock = tx;
                        }

                        if (tracksPerAddressCensorship)
                        {
                            // Finds worst tx in pool to compare with pool transactions of tracked addresses
                            if (_betterTxComparer.Compare(worstTxInBlock, tx) < 0)
                            {
                                worstTxInBlock = tx;
                            }

                            bool trackAddress = _bestTxPerObservedAddresses.ContainsKey(tx.To!);
                            if (trackAddress && trackedAddressesInBlock.Add(tx.To!))
                            {
                                blockTxsOfTrackedAddresses++;
                            }
                        }
                    }
                }

                if (tracksPerAddressCensorship)
                {
                    foreach (Transaction? bestTx in _bestTxPerObservedAddresses.Values)
                    {
                        // if there is no transaction in block or the best tx in the pool is better than the worst tx in the block
                        if (bestTx is null || _betterTxComparer.Compare(bestTx, worstTxInBlock) < 0)
                        {
                            poolTxsThatAreBetterThanWorstInBlock++;
                        }
                    }
                }

                // Checking to see if the block exhibits high-paying tx censorship or address censorship or both.
                // High-paying tx censorship is flagged if the best tx in the pool is not included in the block.
                // Address censorship is flagged if txs sent to less than half of the user-specified addresses
                // for censorship detection with includable txs in the pool are included in the block.
                bool isCensored = _betterTxComparer.Compare(bestTxInBlock, _txPool.GetBestTx()) > 0
                                  || blockTxsOfTrackedAddresses * 2 < poolTxsThatAreBetterThanWorstInBlock;

                BlockCensorshipInfo blockCensorshipInfo = new(isCensored, block.ParentHash);
                BlockNumberHash blockNumberHash = new BlockNumberHash(block);
                _potentiallyCensoredBlocks.Set(blockNumberHash, blockCensorshipInfo);

                if (isCensored)
                {
                    Metrics.NumberOfPotentiallyCensoredBlocks++;
                    Metrics.LastPotentiallyCensoredBlockNumber = block.Number;
                    DetectMultiBlockCensorship(blockNumberHash, blockCensorshipInfo);
                }
            }
        }
        finally
        {
            if (tracksPerAddressCensorship)
            {
                foreach (AddressAsKey key in _bestTxPerObservedAddresses.Keys)
                {
                    _bestTxPerObservedAddresses[key] = null;
                }
            }
        }
    }

    private void DetectMultiBlockCensorship(BlockNumberHash block, BlockCensorshipInfo blockCensorshipInfo)
    {
        if (DetectPastBlockCensorship() && !_censoredBlocks.Contains(block))
        {
            _censoredBlocks.Add(block);
            Metrics.NumberOfCensoredBlocks++;
            Metrics.LastCensoredBlockNumber = block.Number;
            if (_logger.IsInfo) _logger.Info($"Censorship detected for block {block.Number} with hash {block.Hash!}");
        }

        bool DetectPastBlockCensorship()
        {
            // Censorship is detected if potential censorship is flagged for the last _blockCensorshipThreshold blocks including the latest.
            if (block.Number >= _blockCensorshipThreshold)
            {
                long blockNumber = block.Number - 1;
                ValueHash256 parentHash = blockCensorshipInfo.ParentHash!.Value;
                for (int i = 1; i < _blockCensorshipThreshold; i++)
                {
                    BlockCensorshipInfo info = _potentiallyCensoredBlocks.Get(new BlockNumberHash(blockNumber, parentHash));

                    if (!info.IsCensored)
                    {
                        return false;
                    }

                    parentHash = info.ParentHash!.Value;
                    blockNumber--;
                }

                return true;
            }

            return false;
        }
    }

    public IEnumerable<BlockNumberHash> GetCensoredBlocks() => _censoredBlocks;

    public bool BlockPotentiallyCensored(long blockNumber, ValueHash256 blockHash) => _potentiallyCensoredBlocks.Contains(new BlockNumberHash(blockNumber, blockHash));

    public void Dispose()
    {
        _blockProcessor.BlockProcessing -= OnBlockProcessing;
    }
}

public readonly record struct BlockCensorshipInfo(bool IsCensored, ValueHash256? ParentHash);

public readonly record struct BlockNumberHash(long Number, ValueHash256 Hash) : IEquatable<BlockNumberHash>
{
    public BlockNumberHash(Block block) : this(block.Number, block.Hash ?? block.CalculateHash()) { }
}

public class WrapAroundArray<T>(long maxSize = 1) : IEnumerable<T>
{
    private readonly T[] _items = new T[maxSize];
    private long _counter;

    public void Add(T item)
    {
        _items[(int)(_counter % maxSize)] = item;
        _counter++;
    }

    public bool Contains(T item) => _items.Contains(item);

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
