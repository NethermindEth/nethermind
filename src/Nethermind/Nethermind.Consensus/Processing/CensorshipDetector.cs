// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Processing;

public class CensorshipDetector : IDisposable
{
    private readonly ITxPool _txPool;
    private readonly IBlockProcessor _blockProcessor;
    private readonly IBlockTree _blockTree;
    private readonly IAccountStateProvider _accounts;

    private const int CacheSize = 4;
    private readonly LruCache<long, bool>
        _censorshipDetector = new(CacheSize, CacheSize, "censorshipDetector");

    public CensorshipDetector(
        ITxPool txPool,
        IBlockProcessor blockProcessor,
        IBlockTree blockTree,
        IAccountStateProvider accounts)
    {
        _txPool = txPool;
        _blockProcessor = blockProcessor;
        _blockTree = blockTree;
        _accounts = accounts;
        _blockProcessor.BlockProcessing += OnBlockProcessing;
        _blockTree.NewHeadBlock += OnNewHead;
    }

    private void OnBlockProcessing(object? sender, BlockEventArgs e)
    {
        Task.Run(() => Cache(e.Block));
    }

    private void OnNewHead(object? sender, BlockEventArgs e)
    {
        Task.Run(() => Cache(e.Block));
    }

    public void Cache(Block block)
    {
        IDictionary<AddressAsKey, Transaction[]>? txs = _txPool.GetPendingTransactionsBySender();
        Transaction[] blockTransactions = block.Transactions;

        UInt256 maxGasPriceInBlock = 0;
        for (int i = 0; i < blockTransactions.Length; i++)
        {
            maxGasPriceInBlock = UInt256.Max(maxGasPriceInBlock, blockTransactions[i].GasPrice);
        }

        UInt256 maxGasPriceInPool = 0;
        foreach (Transaction tx in from KeyValuePair<AddressAsKey, Transaction[]> txsBySender in txs
                                   let tx = txsBySender.Value[0]
                                   select tx)
        {
            _accounts.TryGetAccount(tx.SenderAddress, out AccountStruct senderAccount);
            if (senderAccount.Nonce == tx.Nonce)
            {
                // check transaction.GasBottleneck, ask Marcin Sobczak
                maxGasPriceInPool = UInt256.Max(tx.GasPrice, maxGasPriceInPool);
            }
        }

        _censorshipDetector.Set(block.Number, maxGasPriceInPool > maxGasPriceInBlock);
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
        _blockTree.NewHeadBlock -= OnNewHead;
    }
}