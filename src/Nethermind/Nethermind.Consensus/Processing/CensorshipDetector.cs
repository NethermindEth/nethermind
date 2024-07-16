// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.TxPool;
using Nethermind.TxPool.Collections;

namespace Nethermind.Blockchain.Blocks;

public class CensorshipDetector : IDisposable
{
    private readonly ITxPool _txPool;
    private readonly IBlockProcessor _blockProcessor;
    private static CensorshipDetector? _instance;

    public static CensorshipDetector Instance()
    {
        _instance ??= new CensorshipDetector();
        return _instance;
    }
    private const int CacheSize = 4;

    private readonly LruCache<long, bool>
        _censorshipDetector = new(CacheSize, CacheSize, "censorshipDetector");

    public CensorshipDetector(ITxPool txPool, IBlockProcessor blockProcessor)
    {
        _txPool = txPool;
        _blockProcessor = blockProcessor;
        _blockProcessor.BlockProcessing += OnBlockProcessing;
    }

    private void OnBlockProcessing(object? sender, BlockEventArgs e)
    {
        Task.Run(() =>
        {
            IDictionary<AddressAsKey, Transaction[]>? txs =_txPool.GetPendingTransactionsBySender();
            UInt256 maxGasPriceInBlock = 0;

            Transaction[] blockTransactions = e.Block.Transactions;
            for (int i = 0; i < blockTransactions.Length; i++)
            {
                maxGasPriceInBlock = UInt256.Max(maxGasPriceInBlock, blockTransactions[i].GasPrice);
            }

            UInt256 maxGasPriceInPool = 0;
            foreach (KeyValuePair<AddressAsKey,Transaction[]> txsBySender in txs)
            {
                Transaction transaction = txsBySender.Value[0];
                // check transaction.GasBottleneck, ask Marcin Sobczak
                maxGasPriceInPool = UInt256.Max(transaction.GasPrice, maxGasPriceInPool);
            }

            _censorshipDetector.Set(e.Block.Number, maxGasPriceInPool > maxGasPriceInBlock);
        });
    }


    private void OnNewHead(object? sender, BlockEventArgs e)
    {
        Task.Run(() => Cache(e.Block));
    }

    public struct TxCensorshipInfo
    {
        public bool PotentialCensorship { get; set; }
        public ValueHash256 TxHash { get; set; }

        public TxCensorshipInfo(bool potentialCensorship, ValueHash256 txHash)
        {
            PotentialCensorship = potentialCensorship;
            TxHash = txHash;
        }
    }

    public CensorshipDetector() { }

    public void Delete(Hash256 blockHash)
    {
        _censorshipDetector.Delete(blockHash);
    }

    public void Cache(Block block)
    {
        UInt256 maxGasPriceInBlock = 0;

        for (int i = 0; i < block.Transactions.Length; i++)
        {
            maxGasPriceInBlock = UInt256.Max(maxGasPriceInBlock, block.Transactions[i].GasPrice);
        }

        IDictionary<AddressAsKey, Transaction[]>? txBySender = _txPool.GetPendingTransactionsBySender();

        TxGasPriceSortedCollection.TxHashGasPricePair pair = TxGasPriceSortedCollection.Instance().GetFirstPair();

        _censorshipDetector.Set(block.Hash, new TxCensorshipInfo(pair.GasPrice > maxGasPriceInBlock, pair.Hash));
    }

    public bool CensorshipDetected()
    {
        bool censoring = false;
        
        TxCensorshipInfo[] suspects = _censorshipDetector.GetValues();
        ValueHash256 toCompare = suspects[0].TxHash;
        foreach (TxCensorshipInfo suspect in suspects)
        {
            if (!suspect.PotentialCensorship || suspect.TxHash != toCompare)
            {
                return false;
            }
        }
        return true;
    }

    public void Dispose()
    {
        _blockProcessor.BlockProcessing _= OnBlockProcessing;
    }
}
