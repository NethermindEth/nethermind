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
            private readonly IStateProvider _stateProvider;
            private readonly IStorageProvider _storageProvider;
            private readonly IBlockProcessor.IBlockTransactionsExecutor _blockTransactionsExecutor;
            private readonly ITransactionProcessorAdapter _executeAdapter;
            private readonly ITransactionProcessorAdapter _buildUpAdapter;
            private readonly ChangeableTransactionProcessorAdapter _changeableTransactionProcessorAdapter;

            public BlockValidationTransactionsExecutor(ITransactionProcessor transactionProcessor, IStateProvider stateProvider, IStorageProvider storageProvider)
            {
                _stateProvider = stateProvider;
                _storageProvider = storageProvider;
                _changeableTransactionProcessorAdapter = new ChangeableTransactionProcessorAdapter(transactionProcessor);
                _executeAdapter = _changeableTransactionProcessorAdapter.CurrentAdapter;
                _buildUpAdapter = new BuildUpTransactionProcessorAdapter(transactionProcessor);
                _blockTransactionsExecutor = new BlockTransactionsExecutor(_changeableTransactionProcessorAdapter, stateProvider);
            }

            public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, IReleaseSpec spec)
            {
                if (spec.IsEip658Enabled)
                {
                    _changeableTransactionProcessorAdapter.CurrentAdapter = _buildUpAdapter;
                    TxReceipt[] receipts =  _blockTransactionsExecutor.ProcessTransactions(block, processingOptions, receiptsTracer, spec);
                    _storageProvider.Commit(receiptsTracer.IsTracingState ? receiptsTracer : NullStorageTracer.Instance);
                    _stateProvider.Commit(spec, receiptsTracer.IsTracingState ? receiptsTracer : NullStateTracer.Instance);
                    return receipts;
                }
                else
                {
                    _changeableTransactionProcessorAdapter.CurrentAdapter = _executeAdapter;
                    return _blockTransactionsExecutor.ProcessTransactions(block, processingOptions, receiptsTracer, spec);
                }
            }

            public event EventHandler<TxProcessedEventArgs>? TransactionProcessed
            {
                add { _blockTransactionsExecutor.TransactionProcessed += value!; }
                remove { _blockTransactionsExecutor.TransactionProcessed -= value!; }
            }
        }
    }
}
