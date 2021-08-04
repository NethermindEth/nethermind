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

namespace Nethermind.Blockchain.Processing
{
    public partial class BlockProcessor
    {
        public class BlockValidationTransactionsExecutor : IBlockProcessor.IBlockTransactionsExecutor
        {
            private readonly ITransactionProcessorAdapter _transactionProcessor;
            private readonly IStateProvider _stateProvider;
        
            public BlockValidationTransactionsExecutor(ITransactionProcessor transactionProcessor, IStateProvider stateProvider)
            {
                _transactionProcessor = new ExecuteTransactionProcessorAdapter(transactionProcessor);
                _stateProvider = stateProvider;
            }
        
            public event EventHandler<TxProcessedEventArgs>? TransactionProcessed; 
        
            public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, IReleaseSpec spec)
            {
                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    Transaction currentTx = block.Transactions[i];
                    ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);
                }
                return receiptsTracer.TxReceipts.ToArray();
            }
        
            private void ProcessTransaction(Block block, Transaction currentTx, int index, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
            {
                _transactionProcessor.ProcessTransaction(block, currentTx, receiptsTracer, processingOptions, _stateProvider);
                TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(index, currentTx, receiptsTracer.TxReceipts[index]));
            }
        }
    }
}
