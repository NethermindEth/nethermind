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
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Mev.Data;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.TxPool;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Mev
{
    public class MevProduceBlockTransactionsStrategy : BlockProcessor.ProduceBlockTransactionsStrategy
    {
        private readonly IStateProvider _stateProvider;
        private readonly IStorageProvider _storageProvider;
        
        public MevProduceBlockTransactionsStrategy(ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv) : 
            this(readOnlyTxProcessingEnv.TransactionProcessor, readOnlyTxProcessingEnv.StateProvider, readOnlyTxProcessingEnv.StorageProvider)
        {
        }

        private MevProduceBlockTransactionsStrategy(
            ITransactionProcessor transactionProcessor, 
            IStateProvider stateProvider,
            IStorageProvider storageProvider) 
            : base(transactionProcessor, stateProvider, storageProvider)
        {
            _stateProvider = stateProvider;
            _storageProvider = storageProvider;
        }
        
        public override TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, IBlockTracer blockTracer, BlockReceiptsTracer receiptsTracer, IReleaseSpec spec)
        {
            IEnumerable<Transaction> transactions = block.GetTransactions();
            LinkedHashSet<Transaction> transactionsInBlock = new(ByHashTxComparer.Instance);
            List<BundleTransaction> bundleTransactions = new();
            Keccak? bundleHash = null;
            
            foreach (Transaction currentTx in transactions)
            {
                if (!transactionsInBlock.Contains(currentTx))
                {
                    // No more gas available in block
                    if (currentTx.GasLimit > block.Header.GasLimit - block.GasUsed)
                    {
                        break;
                    }
                    // if we don't accumulate bundle yet
                    if (bundleHash is null) 
                    {
                        // and we see a bundle transaction
                        if (currentTx is BundleTransaction bundleTransaction) 
                        {
                            // start accumulating the bundle
                            bundleTransactions.Add(bundleTransaction);
                            bundleHash = bundleTransaction.BundleHash;
                        }
                        else
                        {
                            // otherwise process transaction as usual 
                            ProcessTransaction(block, currentTx, transactionsInBlock.Count, receiptsTracer, processingOptions, transactionsInBlock);
                        }
                    }
                    // if we are accumulating bundle
                    else
                    {
                        // if we see a bundle transaction
                        if (currentTx is BundleTransaction bundleTransaction)
                        {
                            // if its from same bundle
                            if (bundleTransaction.BundleHash == bundleHash)
                            {
                                // keep accumulating the bundle
                                bundleTransactions.Add(bundleTransaction);
                            }
                            // if its from different bundle
                            else
                            {
                                // process accumulated bundle
                                ProcessBundle(block, transactionsInBlock, bundleTransactions, receiptsTracer, processingOptions);
                                
                                if (currentTx.GasLimit > block.Header.GasLimit - block.GasUsed)
                                {
                                    break;
                                }
                                
                                // start accumulating new bundle
                                bundleTransactions.Add(bundleTransaction);
                                bundleHash = bundleTransaction.BundleHash;
                            }
                        }
                        // if we see a normal transaction
                        else
                        {
                            // process the bundle and stop accumulating it
                            ProcessBundle(block, transactionsInBlock, bundleTransactions, receiptsTracer, processingOptions);
                            bundleHash = null;
                            
                            if (currentTx.GasLimit > block.Header.GasLimit - block.GasUsed)
                            {
                                break;
                            }
                            
                            // process normal transaction
                            ProcessTransaction(block, currentTx, transactionsInBlock.Count, receiptsTracer, processingOptions, transactionsInBlock);
                        }
                    }
                }
            }
            // if we ended with accumulated bundle, lets process it
            if (bundleTransactions.Count > 0)
            {
                ProcessBundle(block, transactionsInBlock, bundleTransactions, receiptsTracer, processingOptions);
            }

            _stateProvider.Commit(spec);
            _storageProvider.Commit();
            
            block.TrySetTransactions(transactionsInBlock.ToArray());
            return receiptsTracer.TxReceipts.ToArray();
        }

        private void ProcessBundle(Block block,
            LinkedHashSet<Transaction> transactionsInBlock,
            List<BundleTransaction> bundleTransactions,
            BlockReceiptsTracer receiptsTracer, 
            ProcessingOptions processingOptions)
        {
            int stateSnapshot = _stateProvider.TakeSnapshot();
            int storageSnapshot = _storageProvider.TakeSnapshot();
            int receiptSnapshot = receiptsTracer.TakeSnapshot();
            UInt256 initialBalance = _stateProvider.GetBalance(block.Header.GasBeneficiary!);
            
            bool CheckFeeNotManipulated()
            {
                UInt256 finalBalance = _stateProvider.GetBalance(block.Header.GasBeneficiary!);
                UInt256 feeReceived = finalBalance - initialBalance;
                UInt256 originalSimulatedGasPrice = bundleTransactions[0].SimulatedBundleFee / bundleTransactions[0].SimulatedBundleGasUsed;
                UInt256 actualGasPrice = feeReceived / (UInt256) receiptsTracer.LastReceipt.GasUsed!;
                return actualGasPrice < originalSimulatedGasPrice;
            }
            
            bool bundleSucceeded = true;
            for (int index = 0; index < bundleTransactions.Count && bundleSucceeded; index++)
            {
                bundleSucceeded = ProcessBundleTransaction(block, bundleTransactions[index], index, receiptsTracer, processingOptions);
            }
            
            bundleSucceeded &= CheckFeeNotManipulated();

            if (bundleSucceeded)
            {
                for (int index = 0; index < bundleTransactions.Count; index++)
                {
                    BundleTransaction bundleTransaction = bundleTransactions[index];
                    transactionsInBlock.Add(bundleTransaction);
                    int txIndex = receiptSnapshot + index;
                    _transactionProcessed?.Invoke(this, new TxProcessedEventArgs(txIndex, bundleTransaction, receiptsTracer.TxReceipts[txIndex]));
                }
            }
            else
            {
                _stateProvider.Restore(stateSnapshot);
                _storageProvider.Restore(storageSnapshot);
                receiptsTracer.RestoreSnapshot(receiptSnapshot);
                for (int index = 0; index < bundleTransactions.Count; index++)
                {
                    transactionsInBlock.Remove(bundleTransactions[index]);
                }
            }

            bundleTransactions.Clear();
        }

        private bool ProcessBundleTransaction(Block block, BundleTransaction currentTx, int index, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
        {
            ProcessTransaction(block, currentTx, index, receiptsTracer, processingOptions);
            string? error = receiptsTracer.LastReceipt.Error;
            bool transactionSucceeded = string.IsNullOrEmpty(error) || (error == "revert" && currentTx.CanRevert);
            return transactionSucceeded;
        }
    }
}
