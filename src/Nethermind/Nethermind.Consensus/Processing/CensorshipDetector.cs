// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Processing;

public class CensorshipDetector : IDisposable
{
    private readonly ITxPool _txPool;
    private readonly IBlockProcessor _blockProcessor;
    private readonly IBlockTree _blockTree;
    private readonly IAccountStateProvider _accounts;
    private readonly ILogger _logger;

    private const int UnconfirmedBlocksCacheSize = 64;
    private readonly LruCache<long, bool>
        _unconfirmedBlocksCensorshipDetector = new(
            UnconfirmedBlocksCacheSize, UnconfirmedBlocksCacheSize, "unconfirmedBlocksCensorshipDetector"
            );
    private const int CacheSize = 4;
    private readonly LruCache<long, bool>
        _censorshipDetector = new(CacheSize, CacheSize, "censorshipDetector");

    public CensorshipDetector(
        ITxPool txPool,
        IBlockProcessor blockProcessor,
        IBlockTree blockTree,
        IAccountStateProvider accounts,
        ILogManager logManager)
    {
        _txPool = txPool;
        _blockProcessor = blockProcessor;
        _blockTree = blockTree;
        _accounts = accounts;
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        _blockProcessor.BlockProcessing += OnBlockProcessing;
        _blockTree.BlockAddedToMain += OnBlockAdded;
    }

    private void OnBlockProcessing(object? sender, BlockEventArgs e)
    {
        Task.Run(() => PreCachingOperation(e.Block));
    }

    private void OnBlockAdded(object? sender, BlockEventArgs e)
    {
        Task.Run(() => Cache(e.Block));
    }

    private void PreCachingOperation(Block block)
    {
        UInt256 maxGasPriceInBlock = 0;
        Transaction[] blockTransactions = block.Transactions;
        foreach (Transaction tx in blockTransactions)
        {
            if (!tx.SupportsBlobs)
            {
                if (tx.Supports1559)
                {
                    maxGasPriceInBlock = UInt256.Max(maxGasPriceInBlock, tx.GasPrice);
                }
                else
                {
                    maxGasPriceInBlock = UInt256.Max(maxGasPriceInBlock, tx.GasPrice + block.BaseFeePerGas);
                }
            }
        }

        Transaction bestTx = _txPool.GetBestTxOfEachSender().First();
        UInt256 maxGasPriceInPool = bestTx.Supports1559 ? bestTx.GasPrice + block.BaseFeePerGas : bestTx.GasPrice;

        _unconfirmedBlocksCensorshipDetector.Set(block.Number, maxGasPriceInBlock < maxGasPriceInPool);
    }

    private void Cache(Block block)
    {
        _censorshipDetector.Set(block.Number, _unconfirmedBlocksCensorshipDetector.Get(block.Number));
        if (_logger.IsInfo)
        {
            _logger.Info($"Potential Censorship detected for Block No. {block.Number}");
        }
    }

    public bool CensorshipDetected
    {
        get
        {
            return _censorshipDetector.GetValues().All(s => s);
        }
    }

    public void Dispose()
    {
        _blockProcessor.BlockProcessing -= OnBlockProcessing;
        _blockTree.NewHeadBlock -= OnBlockAdded;
    }
}
