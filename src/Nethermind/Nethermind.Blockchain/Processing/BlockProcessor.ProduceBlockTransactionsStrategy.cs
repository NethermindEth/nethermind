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
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.TxPool;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Blockchain.Processing
{
    public partial class BlockProcessor
    {
        public class ProduceBlockTransactionsStrategy : IProduceBlockTransactionsStrategy
        {
            private readonly ITransactionProcessorAdapter _transactionProcessor;
            private readonly IStateProvider _stateProvider;
            private readonly IStorageProvider _storageProvider;

            public ProduceBlockTransactionsStrategy(ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv) : 
                this(readOnlyTxProcessingEnv.TransactionProcessor, readOnlyTxProcessingEnv.StateProvider, readOnlyTxProcessingEnv.StorageProvider)
            {
                
            }
            
            public ProduceBlockTransactionsStrategy(
                ITransactionProcessor transactionProcessor,
                IStateProvider stateProvider,
                IStorageProvider storageProvider)
            {
                _transactionProcessor = new BuildUpTransactionProcessorAdapter(transactionProcessor);
                _stateProvider = stateProvider;
                _storageProvider = storageProvider;
            }

            protected EventHandler<TxProcessedEventArgs>? _transactionProcessed;
            event EventHandler<TxProcessedEventArgs>? IBlockProcessor.IBlockTransactionsStrategy.TransactionProcessed
            {
                add => _transactionProcessed += value;
                remove => _transactionProcessed -= value;
            }

            private event EventHandler<TxCheckEventArgs>? CheckTransaction;
            event EventHandler<TxCheckEventArgs>? IProduceBlockTransactionsStrategy.CheckTransaction
            {
                add => CheckTransaction += value;
                remove => CheckTransaction -= value;
            }

            public virtual TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, IBlockTracer blockTracer, BlockReceiptsTracer receiptsTracer, IReleaseSpec spec)
            {
                IEnumerable<Transaction> transactions = block.GetTransactions();

                int i = 0;
                LinkedHashSet<Transaction> transactionsInBlock = new(ByHashTxComparer.Instance);
                foreach (Transaction currentTx in transactions)
                {
                    TxCheckEventArgs args = CheckTx(transactionsInBlock, currentTx, block);
                    if (args.Action == TxAction.Add)
                    {
                        ProcessTransaction(block, currentTx, i++, receiptsTracer, processingOptions, transactionsInBlock);
                    }
                    else if (args.Action == TxAction.Stop)
                    {
                        break;
                    }
                }

                _stateProvider.Commit(spec);
                _storageProvider.Commit();

                block.TrySetTransactions(transactionsInBlock.ToArray());
                return receiptsTracer.TxReceipts.ToArray();
            }

            protected void ProcessTransaction(Block block, Transaction currentTx, int index, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions, ISet<Transaction>? transactionsInBlock = null)
            {
                _transactionProcessor.ProcessTransaction(block, currentTx, receiptsTracer, processingOptions, _stateProvider);
                
                if (transactionsInBlock is not null)
                {
                    transactionsInBlock.Add(currentTx);
                    _transactionProcessed?.Invoke(this, new TxProcessedEventArgs(index, currentTx, receiptsTracer.TxReceipts[index]));
                }
            }

            private TxCheckEventArgs CheckTx(IReadOnlySet<Transaction> transactionsInBlock, Transaction currentTx, Block block)
            {
                TxCheckEventArgs args = new(transactionsInBlock.Count, currentTx, block, transactionsInBlock);

                if (transactionsInBlock.Contains(currentTx))
                {
                    return args.Set(TxAction.Skip, "Transaction already in block.");
                }

                // No more gas available in block
                long gasRemaining = block.Header.GasLimit - block.GasUsed;
                if (currentTx.GasLimit > gasRemaining)
                {
                    return args.Set(TxAction.Stop, "Not enough gas in block.");
                }

                CheckTransaction?.Invoke(this, args);
                return args;
            }
        }
    }
}
