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
using Nethermind.Evm.Tracing;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.TxPool;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Blockchain.Processing
{
    public partial class BlockProcessor
    {
        public interface IProduceBlockTransactionsStrategy : IBlockProcessor.IBlockTransactionsStrategy
        {
            event EventHandler<TxCheckEventArgs> CheckTransaction;
        }

        public class TxCheckEventArgs : TxEventArgs
        {
            public Block Block { get; }
            public IReadOnlyCollection<Transaction> TransactionsInBlock { get; }
            public TxAction Action { get; private set; } = TxAction.Add;
            public string Reason { get; private set; } = string.Empty;

            public TxCheckEventArgs Set(TxAction action, string reason)
            {
                Action = action;
                Reason = reason;
                return this;
            }

            public TxCheckEventArgs(int index, Transaction transaction, Block block, IReadOnlyCollection<Transaction> transactionsInBlock) 
                : base(index, transaction)
            {
                Block = block;
                TransactionsInBlock = transactionsInBlock;
            }
        }
        
        public class ProduceBlockTransactionsStrategy : IProduceBlockTransactionsStrategy
        {
            private readonly Evm.ITransactionProcessor _transactionProcessor;
            private readonly IStateProvider _stateProvider;
            private readonly IStorageProvider _storageProvider;

            public ProduceBlockTransactionsStrategy(ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv) : 
                this(readOnlyTxProcessingEnv.TransactionProcessor, readOnlyTxProcessingEnv.StateProvider, readOnlyTxProcessingEnv.StorageProvider)
            {
                
            }
            
            public ProduceBlockTransactionsStrategy(
                Evm.ITransactionProcessor transactionProcessor,
                IStateProvider stateProvider,
                IStorageProvider storageProvider)
            {
                _transactionProcessor = transactionProcessor;
                _stateProvider = stateProvider;
                _storageProvider = storageProvider;
            }

            public event EventHandler<TxProcessedEventArgs>? TransactionProcessed;
            public event EventHandler<TxCheckEventArgs>? CheckTransaction;

            public virtual TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, IBlockTracer blockTracer, BlockReceiptsTracer receiptsTracer, IReleaseSpec spec)
            {
                IEnumerable<Transaction> transactions = block.GetTransactions(out _);

                int i = 0;
                LinkedHashSet<Transaction> transactionsInBlock = new(ByHashTxComparer.Instance);
                foreach (Transaction currentTx in transactions)
                {
                    TxCheckEventArgs args = CheckTx(transactionsInBlock, currentTx, block);
                    if (args.Action == TxAction.Add)
                    {
                        ProcessTransaction(block, transactionsInBlock, currentTx, i++, receiptsTracer, processingOptions);
                        transactionsInBlock.Add(currentTx);
                    }
                    else if (args.Action == TxAction.Stop)
                    {
                        break;
                    }
                }

                _stateProvider.Commit(spec);
                _storageProvider.Commit();

                block.TrySetTransactions(transactionsInBlock.ToArray());
                block.Header.TxRoot = new TxTrie(block.Transactions).RootHash;
                return receiptsTracer.TxReceipts!;
            }

            protected void ProcessTransaction(Block block, ISet<Transaction>? transactionsInBlock, Transaction currentTx, int index, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
            {
                if ((processingOptions & ProcessingOptions.DoNotVerifyNonce) != 0)
                {
                    currentTx.Nonce = _stateProvider.GetNonce(currentTx.SenderAddress);
                }

                receiptsTracer.StartNewTxTrace(currentTx);
                _transactionProcessor.BuildUp(currentTx, block.Header, receiptsTracer);
                receiptsTracer.EndTxTrace();

                transactionsInBlock?.Add(currentTx);
                TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(index, currentTx, receiptsTracer.TxReceipts[index]));
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
