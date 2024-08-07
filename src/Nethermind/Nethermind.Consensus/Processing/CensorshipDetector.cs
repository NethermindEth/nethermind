// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Processing;

public class CensorshipDetector : IDisposable
{
    private readonly ITxPool _txPool;
    private readonly IComparer<Transaction> _comparer;
    private readonly IBlockProcessor _blockProcessor;
    private readonly IBlockTree _blockTree;
    private readonly ILogger _logger;
    private readonly LruCache<long, bool> _temporaryCensorshipDetector;
    private readonly LruCache<long, bool> _censorshipDetector;
    private const int _temporaryCacheSize = 16;
    private const int _cacheSize = 4;

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
        _temporaryCensorshipDetector = new(
            _temporaryCacheSize, _temporaryCacheSize, "temporaryCensorshipDetector"
            );
        _censorshipDetector = new(_cacheSize, _cacheSize, "censorshipDetector");
        _blockProcessor.BlockProcessing += OnBlockProcessing;
        _blockTree.BlockAddedToMain += OnBlockAddedToMain;
    }

    private void OnBlockProcessing(object? sender, BlockEventArgs e)
    {
        Task.Run(() => PreCachingOperation(e.Block));
    }

    private void OnBlockAddedToMain(object? sender, BlockReplacementEventArgs e)
    {
        Task.Run(() => Cache(e.Block));
    }

    private void PreCachingOperation(Block block)
    {
        Transaction bestTxInBlock = new Transaction();

        foreach (Transaction tx in block.Transactions)
        {
            if (!tx.SupportsBlobs && _comparer.Compare(bestTxInBlock, tx) > 0)
            {
                bestTxInBlock = tx;
            }
        }

        Transaction bestTxInPool = _txPool.GetBestTx();

        bool unconfirmedBlockPotentialCensorship = _comparer.Compare(bestTxInBlock, bestTxInPool) > 0;

        _temporaryCensorshipDetector.Set(block.Number, unconfirmedBlockPotentialCensorship);
    }

    private void Cache(Block block)
    {
        if (_temporaryCensorshipDetector.TryGet(block.Number, out bool potentialCensorship))
        {
            _censorshipDetector.Set(block.Number, potentialCensorship);
            if (potentialCensorship)
            {
                if (_logger.IsInfo)
                {
                    _logger.Info($"Potential Censorship detected for Block No. {block.Number}");
                }
            }
        }
    }

    public bool CensorshipDetected
    {
        get
        {
            return _censorshipDetector.GetValues().All(s => s);
        }
    }

    public bool GetTemporaryCensorshipStatus(long blockNumber) => _temporaryCensorshipDetector.Get(blockNumber);

    public bool GetCensorshipStatus(long blockNumber) => _censorshipDetector.Get(blockNumber);

    public bool TemporaryCacheContainsBlock(long blockNumber) => _temporaryCensorshipDetector.Contains(blockNumber);

    public bool CacheContainsBlock(long blockNumber) => _censorshipDetector.Contains(blockNumber);

    public void Dispose()
    {
        _blockProcessor.BlockProcessing -= OnBlockProcessing;
        _blockTree.BlockAddedToMain -= OnBlockAddedToMain;
    }
}
