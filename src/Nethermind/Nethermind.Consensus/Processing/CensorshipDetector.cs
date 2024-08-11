// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Processing;

public class CensorshipDetector : IDisposable
{
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
            Hash = block.CalculateHash();
        }
    }

    public readonly struct PotentialCensorship
    {
        public bool HighPayingTxCensorship { get; }
        public bool AddressCensorship { get; }

        public PotentialCensorship(bool highPayingTxCensorship, bool addressCensorship)
        {
            HighPayingTxCensorship = highPayingTxCensorship;
            AddressCensorship = addressCensorship;
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

    private readonly ITxPool _txPool;
    private readonly IComparer<Transaction> _comparer;
    private readonly IBlockProcessor _blockProcessor;
    private readonly IBlockTree _blockTree;
    private readonly ILogger _logger;
    private readonly LruCache<BlockNumberHash, PotentialCensorship> _temporaryPotentialCensoredBlocks;
    private readonly LruCache<BlockNumberHash, BlockCensorshipInfo> _potentialCensoredBlocks;
    private IEnumerable<BlockNumberHash> _censoredBlocks = [];
    private int _censoredBlocksCount = 0;
    private const int _cacheSize = 64;

    public CensorshipDetector(
        ITxPool txPool,
        IComparer<Transaction> comparer,
        IBlockProcessor blockProcessor,
        IBlockTree blockTree,
        ILogManager logManager)
    {
        _txPool = txPool;
        _comparer = comparer;
        _blockProcessor = blockProcessor;
        _blockTree = blockTree;
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        _temporaryPotentialCensoredBlocks = new(_cacheSize, _cacheSize, "temporaryPotentialCensoredBlocks");
        _potentialCensoredBlocks = new(_cacheSize, _cacheSize, "potentialCensoredBlocks");
        _blockProcessor.BlockProcessing += OnBlockProcessing;
        _blockTree.BlockAddedToMain += OnBlockAddedToMain;
    }

    private void OnBlockProcessing(object? sender, BlockEventArgs e)
    {
        Task.Run(() => PrepareForCache(e.Block));
    }

    private void OnBlockAddedToMain(object? sender, BlockReplacementEventArgs e)
    {
        Task.Run(() => Cache(e.Block));
    }

    private void PrepareForCache(Block block)
    {
        Transaction bestTxInPool = _txPool.GetBestTx();
        int _poolUniqueAddressesTxSentToCount = _txPool.GetUniqueAddressesTxSentToCount();

        Transaction bestTxInBlock = block.Transactions[0];
        SortedDictionary<Address, bool> _blockAddressCensorshipDetectorHelper = [];
        // Count of how many unique addresses specified by the user are transactions sent to in the block
        int _blockUniqueAddressesTxSentToCount = 0;

        foreach (Transaction tx in block.Transactions)
        {
            if (!tx.SupportsBlobs)
            {
                if (_txPool.DetectingCensorshipForAddress(tx.To!))
                {
                    if (!_blockAddressCensorshipDetectorHelper.TryGetValue(tx.To!, out _))
                    {
                        _blockAddressCensorshipDetectorHelper[tx.To!] = true;
                        _blockUniqueAddressesTxSentToCount++;
                    }
                }

                if (_comparer.Compare(bestTxInBlock, tx) > 0)
                {
                    bestTxInBlock = tx;
                }
            }
        }

        bool _temporaryHighPayingTxCensorship = _comparer.Compare(bestTxInBlock, bestTxInPool) > 0;
        bool _temporaryAddressCensorship = _blockUniqueAddressesTxSentToCount * 2 < _poolUniqueAddressesTxSentToCount;

        var _temporaryPotentialCensorship = new PotentialCensorship(_temporaryHighPayingTxCensorship, _temporaryAddressCensorship);

        _temporaryPotentialCensoredBlocks.Set(new BlockNumberHash(block), _temporaryPotentialCensorship);
    }

    private void Cache(Block block)
    {
        if (_temporaryPotentialCensoredBlocks.TryGet(new BlockNumberHash(block), out PotentialCensorship potentialCensorship))
        {
            bool isCensored = potentialCensorship.HighPayingTxCensorship || potentialCensorship.AddressCensorship;
            _potentialCensoredBlocks.Set(new BlockNumberHash(block), new BlockCensorshipInfo(isCensored, block.ParentHash));
            if (potentialCensorship.HighPayingTxCensorship)
            {
                if (_logger.IsInfo)
                {
                    _logger.Info($"Potential Censorship detected for Block No. {block.Number}\nReason: High-paying Transactions Not Being Included");
                }
            }
            if (potentialCensorship.AddressCensorship)
            {
                if (_logger.IsInfo)
                {
                    _logger.Info($"Potential Censorship detected for Block No. {block.Number}\nReason: Transactions Sent To Select Addresses Not Being Included");
                }
            }
        }
    }

    public bool CensorshipDetected(long number, Hash256 hash)
    {
        if (number > 2)
        {
            BlockCensorshipInfo b0 = _potentialCensoredBlocks.Get(new BlockNumberHash(number, hash));
            BlockCensorshipInfo b1 = _potentialCensoredBlocks.Get(new BlockNumberHash(number - 1, b0.ParentHash));
            BlockCensorshipInfo b2 = _potentialCensoredBlocks.Get(new BlockNumberHash(number - 2, b1.ParentHash));
            BlockCensorshipInfo b3 = _potentialCensoredBlocks.Get(new BlockNumberHash(number - 3, b2.ParentHash));

            bool censorshipDetected = b0.IsCensored && b1.IsCensored && b2.IsCensored && b3.IsCensored;

            if (censorshipDetected)
            {
                _censoredBlocks = _censoredBlocks.Append(new BlockNumberHash(number, hash));
                _censoredBlocksCount++;
                return true;
            }
        }

        return false;
    }

    public bool CensorshipDetected(Block block)
    {
        return CensorshipDetected(block.Number, block.CalculateHash());
    }

    public void AddAddressesToDetectCensorshipFor(IEnumerable<Address> addresses)
    {
        _txPool.AddAddressesToDetectCensorshipFor(addresses);
    }

    public void RemoveAddressesToDetectCensorshipFor(IEnumerable<Address> addresses)
    {
        _txPool.RemoveAddressesToDetectCensorshipFor(addresses);
    }

    public IEnumerable<BlockNumberHash> GetCensoredBlocks()
    {
        return _censoredBlocks;
    }

    public int GetCensoredBlocksCount()
    {
        return _censoredBlocksCount;
    }

    public bool TemporaryPotentialCacheContainsBlock(long blockNumber, Hash256 blockHash) => _temporaryPotentialCensoredBlocks.Contains(new BlockNumberHash(blockNumber, blockHash));

    public bool PotentialCacheContainsBlock(long blockNumber, Hash256 blockHash) => _potentialCensoredBlocks.Contains(new BlockNumberHash(blockNumber, blockHash));

    public void Dispose()
    {
        _blockProcessor.BlockProcessing -= OnBlockProcessing;
        _blockTree.BlockAddedToMain -= OnBlockAddedToMain;
    }
}
