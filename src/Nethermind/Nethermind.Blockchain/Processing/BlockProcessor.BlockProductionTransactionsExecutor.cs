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
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.TxPool;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Blockchain.Processing
{
    public partial class BlockProcessor
    {
        public class BlockProductionTransactionsExecutor : IBlockProductionTransactionsExecutor
        {
            private readonly ITransactionProcessorAdapter _transactionProcessor;
            private readonly IStateProvider _stateProvider;
            private readonly IStorageProvider _storageProvider;
            private readonly BlockProductionTransactionPicker _blockProductionTransactionPicker;
            private readonly ILogger _logger;

            public BlockProductionTransactionsExecutor(
                ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv,
                ISpecProvider specProvider,
                ILogManager logManager) 
                : this(
                    readOnlyTxProcessingEnv.TransactionProcessor, 
                    readOnlyTxProcessingEnv.StateProvider, 
                    readOnlyTxProcessingEnv.StorageProvider, 
                    specProvider, 
                    logManager)
            {
            }
            
            public BlockProductionTransactionsExecutor(
                ITransactionProcessor transactionProcessor,
                IStateProvider stateProvider,
                IStorageProvider storageProvider, 
                ISpecProvider specProvider,
                ILogManager logManager)
            {
                _transactionProcessor = new BuildUpTransactionProcessorAdapter(transactionProcessor);
                _stateProvider = stateProvider;
                _storageProvider = storageProvider;
                _blockProductionTransactionPicker = new BlockProductionTransactionPicker(specProvider);
                _logger = logManager.GetClassLogger();
            }

            protected EventHandler<TxProcessedEventArgs>? _transactionProcessed;
            event EventHandler<TxProcessedEventArgs>? IBlockProcessor.IBlockTransactionsExecutor.TransactionProcessed
            {
                add => _transactionProcessed += value;
                remove => _transactionProcessed -= value;
            }

            event EventHandler<AddingTxEventArgs>? IBlockProductionTransactionsExecutor.AddingTransaction
            {
                add => _blockProductionTransactionPicker.AddingTransaction += value;
                remove => _blockProductionTransactionPicker.AddingTransaction -= value;
            }

            public virtual TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, IReleaseSpec spec)
            {
                IEnumerable<Transaction> transactions = GetTransactions(block);

                int i = 0;
                LinkedHashSet<Transaction> transactionsInBlock = new(ByHashTxComparer.Instance);
                foreach (Transaction currentTx in transactions)
                {
                    TxAction action = ProcessTransaction(block, currentTx, i++, receiptsTracer, processingOptions, transactionsInBlock);
                    if (action == TxAction.Stop) break;
                }

                _stateProvider.Commit(spec, receiptsTracer);
                _storageProvider.Commit(receiptsTracer);

                SetTransactions(block, transactionsInBlock);
                return receiptsTracer.TxReceipts.ToArray();
            }

            protected TxAction ProcessTransaction(
                Block block, 
                Transaction currentTx, 
                int index, 
                BlockReceiptsTracer receiptsTracer,
                ProcessingOptions processingOptions, 
                LinkedHashSet<Transaction> transactionsInBlock,
                bool addToBlock = true)
            {
                AddingTxEventArgs args = _blockProductionTransactionPicker.CanAddTransaction(block, currentTx, transactionsInBlock, _stateProvider);

                if (args.Action != TxAction.Add)
                {
                    if (_logger.IsDebug) _logger.Debug($"Skipping transaction {currentTx.ToShortString()} because: {args.Reason}.");
                }
                else
                {
                    _transactionProcessor.ProcessTransaction(block, currentTx, receiptsTracer, processingOptions, _stateProvider);
                    
                    if (addToBlock)
                    {
                        transactionsInBlock.Add(currentTx);
                        _transactionProcessed?.Invoke(this, new TxProcessedEventArgs(index, currentTx, receiptsTracer.TxReceipts[index]));
                    }
                }
                
                return args.Action;
            }
            
            protected static IEnumerable<Transaction> GetTransactions(Block block) => block.GetTransactions();
            
            protected static void SetTransactions(Block block, IEnumerable<Transaction> transactionsInBlock)
            {
                block.TrySetTransactions(transactionsInBlock.ToArray());
            }
        }
    }
}
