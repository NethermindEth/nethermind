// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor
{
    public class BlockStatelessValidationTransactionsExecutor : IBlockProcessor.IBlockTransactionsExecutor
    {
        private ITransactionProcessorAdapter _transactionProcessor;
        private IWorldState? _stateProvider;

        public BlockStatelessValidationTransactionsExecutor(ITransactionProcessor transactionProcessor)
            : this(new ExecuteTransactionProcessorAdapter(transactionProcessor))
        {
        }

        public BlockStatelessValidationTransactionsExecutor(ITransactionProcessorAdapter transactionProcessor)
        {
            _transactionProcessor = transactionProcessor;
            _stateProvider = null;
        }

        public BlockStatelessValidationTransactionsExecutor(ITransactionProcessor transactionProcessor, IWorldState worldState)
            : this(new ExecuteTransactionProcessorAdapter(transactionProcessor), worldState)
        {
        }

        public BlockStatelessValidationTransactionsExecutor(ITransactionProcessorAdapter transactionProcessor, IWorldState worldState)
        {
            _transactionProcessor = transactionProcessor;
            _stateProvider = worldState;
        }

        public event EventHandler<TxProcessedEventArgs>? TransactionProcessed;

        public IBlockProcessor.IBlockTransactionsExecutor WithNewStateProvider(IWorldState worldState)
        {
            _transactionProcessor = _transactionProcessor.WithNewStateProvider(worldState);
            return new BlockStatelessValidationTransactionsExecutor(_transactionProcessor, worldState);
        }

        public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, IReleaseSpec spec)
        {
            // var ecdsa = new EthereumEcdsa(69420, LimboLogs.Instance);
            if (!block.IsGenesis)
            {

                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    Transaction currentTx = block.Transactions[i];
                    // currentTx.SenderAddress = ecdsa.RecoverAddress(currentTx);
                    ProcessTransaction(block, currentTx, i, receiptsTracer, _stateProvider, processingOptions);
                }
                _stateProvider.Commit(spec);
                _stateProvider.RecalculateStateRoot();
            }
            return receiptsTracer.TxReceipts.ToArray();
        }

        private void ProcessTransaction(Block block, Transaction currentTx, int index, BlockReceiptsTracer receiptsTracer, IWorldState worldState, ProcessingOptions processingOptions)
        {
            _transactionProcessor.ProcessTransaction(block, currentTx, receiptsTracer, processingOptions, worldState);
            TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(index, currentTx, receiptsTracer.TxReceipts[index]));
        }
    }
}
