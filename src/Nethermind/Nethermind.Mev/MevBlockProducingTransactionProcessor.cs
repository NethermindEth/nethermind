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
using Nethermind.Int256;
using Nethermind.Mev.Data;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.TxPool;

namespace Nethermind.Mev
{
    public class MevBlockProducingTransactionProcessor : BlockProcessor.BlockProducingTransactionProcessor
    {
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly IStateProvider _stateProvider;
        private readonly IStorageProvider _storageProvider;
        
        public MevBlockProducingTransactionProcessor(
            ITransactionProcessor transactionProcessor, 
            IStateProvider stateProvider,
            IStorageProvider storageProvider) : base(transactionProcessor, stateProvider, storageProvider)
        {
            _transactionProcessor = transactionProcessor;
            _stateProvider = stateProvider;
            _storageProvider = storageProvider;
        }
        
        public override TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, IBlockTracer blockTracer, BlockReceiptsTracer receiptsTracer, IReleaseSpec spec)
        {
            IEnumerable<Transaction> transactions = block.GetTransactions(out _);

            LinkedHashSet<Transaction> transactionsInBlock = new(DistinctCompareTx.Instance);

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
                    if (bundleHash is null)
                    {
                        if (currentTx is BundleTransaction bundleTransaction)
                        {
                            bundleTransactions.Add(bundleTransaction);
                            bundleHash = bundleTransaction.BundleHash;
                        }
                        else
                        {
                            ProcessTransaction(block, transactionsInBlock, currentTx, transactionsInBlock.Count, receiptsTracer, processingOptions);
                        }
                    }
                    else
                    {
                        if (currentTx is BundleTransaction bundleTransaction)
                        {
                            if (bundleTransaction.BundleHash == bundleHash)
                            {
                                bundleTransactions.Add(bundleTransaction);
                            }
                            else
                            {
                                ProcessBundle(block, transactionsInBlock, bundleTransactions, receiptsTracer, processingOptions);
                                
                                if (currentTx.GasLimit > block.Header.GasLimit - block.GasUsed)
                                {
                                    break;
                                }
                                
                                bundleTransactions.Add(bundleTransaction);
                                bundleHash = bundleTransaction.BundleHash;
                            }
                        }
                        else
                        {
                            ProcessBundle(block, transactionsInBlock, bundleTransactions, receiptsTracer, processingOptions);
                            bundleHash = null;
                            
                            if (currentTx.GasLimit > block.Header.GasLimit - block.GasUsed)
                            {
                                break;
                            }
                            
                            // process current transactions
                            ProcessTransaction(block, transactionsInBlock, currentTx, transactionsInBlock.Count, receiptsTracer, processingOptions);
                        }
                    }
                }
            }
            // if bundle is not clear process it still
            if (bundleTransactions.Count > 0)
            {
                ProcessBundle(block, transactionsInBlock, bundleTransactions, receiptsTracer, processingOptions);
            }
            block.TrySetTransactions(transactionsInBlock.ToArray());
            _stateProvider.Commit(spec);
            _storageProvider.Commit();
            block.Header.TxRoot = new TxTrie(block.Transactions).RootHash;
            return receiptsTracer.TxReceipts!;
        }

        private void ProcessBundle(Block block, LinkedHashSet<Transaction> transactionsInBlock, List<BundleTransaction> bundleTransactions,
            BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
        {
            int stateSnapshot = _stateProvider.TakeSnapshot();
            int storageSnapshot = _storageProvider.TakeSnapshot();
            int receiptSnapshot = receiptsTracer.TakeSnapshot();
            List<TxProcessedEventArgs> eventList = new();
            bool bundleSucceeded = true;
            
            UInt256 initialBalance = _stateProvider.GetBalance(block.Header.GasBeneficiary!);
            for (int index = 0; index < bundleTransactions.Count && bundleSucceeded; index++)
            {
                BundleTransaction currentTx = bundleTransactions[index];
                ProcessTransaction(block, null, currentTx, index, receiptsTracer, processingOptions);

                string error = receiptsTracer.LastReceipt!.Error ?? "";
                bool wasReverted = error == "revert";
                if (wasReverted && !currentTx.CanRevert)
                {
                    bundleSucceeded = false;
                }
                else if (!wasReverted && (error != ""))
                {
                    bundleSucceeded = false;
                }
                else
                {
                    eventList.Add(new TxProcessedEventArgs(transactionsInBlock.Count, currentTx, receiptsTracer.TxReceipts![transactionsInBlock.Count]));                    
                }
            }
            UInt256 finalBalance = _stateProvider.GetBalance(block.Header.GasBeneficiary!);
            UInt256 feeReceived = finalBalance - initialBalance;
            UInt256 originalSimulatedGasPrice = bundleTransactions[0].SimulatedBundleFee / bundleTransactions[0].SimulatedBundleGasUsed;
            UInt256 actualGasPrice = feeReceived / (UInt256)receiptsTracer.LastReceipt!.GasUsed!;

            if (actualGasPrice < originalSimulatedGasPrice)
            {
                bundleSucceeded = false;
            }

            if (bundleSucceeded)
            {
                for (int index = 0; index < eventList.Count; index++)
                {
                    TxProcessedEventArgs eventItem = eventList[index];
                    transactionsInBlock.Add(eventItem.Transaction);
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
    }
}
