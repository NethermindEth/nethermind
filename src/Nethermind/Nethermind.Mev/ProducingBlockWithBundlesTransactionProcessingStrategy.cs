//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Mev.Data;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.TxPool;

namespace Nethermind.Mev
{
    public class ProducingBlockWithBundlesTransactionProcessingStrategy : ITransactionProcessingStrategy // WIP
    {
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly IStateProvider _stateProvider;
        private readonly IStorageProvider _storageProvider;
        private readonly ProcessingOptions _options;
        
        public ProducingBlockWithBundlesTransactionProcessingStrategy(
            ITransactionProcessor transactionProcessor, 
            IStateProvider stateProvider,
            IStorageProvider storageProvider, 
            ProcessingOptions options)
        {
            _transactionProcessor = transactionProcessor;
            _stateProvider = stateProvider;
            _storageProvider = storageProvider;
            _options = options;
        }
        
        public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, IBlockTracer blockTracer, BlockReceiptsTracer receiptsTracer, IReleaseSpec spec, EventHandler<TxProcessedEventArgs> TransactionProcessed)
        {
            IEnumerable<Transaction> transactions = block.GetTransactions(out _);

            int i = 0;
            LinkedHashSet<Transaction> transactionsForBlock = new(DistinctCompareTx.Instance);

            List<(BundleTransaction, int)> bundleTransactionWithIndex = new();
            Keccak? bundleHash = null;
            
            foreach (Transaction currentTx in transactions)
            {
                if (!transactionsForBlock.Contains(currentTx))
                {
                    // No more gas available in block
                    if (currentTx.GasLimit > block.Header.GasLimit - block.GasUsed)
                    {
                        break;
                    }
                    if (bundleHash is null)
                    {
                        if (currentTx is BundleTransaction bundleTransaction)
                        {
                            bundleTransactionWithIndex.Add((bundleTransaction, i++));
                            bundleHash = bundleTransaction.BundleHash;
                        }
                        else
                        {
                            ProcessTransaction(block, currentTx, i++, receiptsTracer, TransactionProcessed);
                            transactionsForBlock.Add(currentTx);
                        }
                    }
                    else
                    {
                        if (currentTx is BundleTransaction bundleTransaction)
                        {
                            if (bundleTransaction.Hash == bundleHash)
                            {
                                bundleTransactionWithIndex.Add((bundleTransaction, i++));
                            }
                            else
                            {
                                // process bundle(!)
                                ProcessBundle(block, bundleTransactionWithIndex, receiptsTracer, TransactionProcessed);

                                bundleTransactionWithIndex.Clear();
                                bundleHash = bundleTransaction.Hash;
                            }
                        }
                        else
                        {
                            // process bundle(!)
                            ProcessBundle(block, bundleTransactionWithIndex, receiptsTracer, TransactionProcessed);

                            bundleHash = null;
                            bundleTransactionWithIndex.Clear();
                            // process current transactions
                            ProcessTransaction(block, currentTx, i++, receiptsTracer, TransactionProcessed);
                            transactionsForBlock.Add(currentTx);
                        }
                    }
                }
            }
            // if bundle is not clear process it still
            if (bundleTransactionWithIndex.Count > 0)
            {
                ProcessBundle(block, bundleTransactionWithIndex, receiptsTracer, TransactionProcessed);
            }
            block.TrySetTransactions(transactionsForBlock.ToArray());
            block.Header.TxRoot = new TxTrie(block.Transactions).RootHash;
            return receiptsTracer.TxReceipts!;
        }
        
        private void ProcessTransaction(Block block, Transaction currentTx, int index, BlockReceiptsTracer receiptsTracer, EventHandler<TxProcessedEventArgs> TransactionProcessed)
        {
            if ((_options & ProcessingOptions.DoNotVerifyNonce) != 0)
            {
                currentTx.Nonce = _stateProvider.GetNonce(currentTx.SenderAddress!);
            }

            receiptsTracer.StartNewTxTrace(currentTx);
            _transactionProcessor.Execute(currentTx, block.Header, receiptsTracer);
            receiptsTracer.EndTxTrace();

            TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(index, currentTx, receiptsTracer.TxReceipts![index]));
        }

        private void ProcessBundle(Block block, List<(BundleTransaction, int)> bundleTransactionWithIndex,
            BlockReceiptsTracer receiptsTracer, EventHandler<TxProcessedEventArgs> TransactionProcessed)
        { 
            var stateSnapshot = _stateProvider.TakeSnapshot();
            var storageSnapshot = _storageProvider.TakeSnapshot();
            //var initialTracer = receiptsTracer;

            var eventList = new List<TxProcessedEventArgs>();
                
            foreach (var (currentTx, index) in bundleTransactionWithIndex)
            {
                if ((_options & ProcessingOptions.DoNotVerifyNonce) != 0)
                {
                    currentTx.Nonce = _stateProvider.GetNonce(currentTx.SenderAddress!);
                }
                receiptsTracer.StartNewTxTrace(currentTx);
                _transactionProcessor.Execute(currentTx, block.Header, receiptsTracer);
                receiptsTracer.EndTxTrace();
                if (receiptsTracer.LastReceipt!.Error == "reverted" && !currentTx.CanRevert)
                {
                    _stateProvider.Restore(stateSnapshot);
                    _storageProvider.Restore(storageSnapshot);
                    return;
                }
                eventList.Add(new TxProcessedEventArgs(index, currentTx, receiptsTracer.TxReceipts![index]));
            }

            foreach (var eventItem in eventList)
            {
                TransactionProcessed?.Invoke(this, eventItem);
            }
        }
    }
}
