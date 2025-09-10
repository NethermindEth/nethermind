// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Processing
{
    /// <summary>
    /// Works much like a <see cref="ITransactionProcessor"/> but at a block level.
    /// Also, like <see cref="ITransactionProcessor"/>, does not prepare the world state. The world state is assumed
    /// to be prepared ahead of time.
    /// </summary>
    public interface IBlockProcessor
    {
        public (Block Block, TxReceipt[] Receipts) ProcessOne(
            Block suggestedBlock,
            ProcessingOptions options,
            IBlockTracer blockTracer,
            IReleaseSpec spec,
            CancellationToken token = default);

        /// <summary>
        /// Fired after a transaction has been processed (even if inside the block).
        /// </summary>
        event EventHandler<TxProcessedEventArgs> TransactionProcessed;

        public interface IBlockTransactionsExecutor
        {
            TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token = default);
            event EventHandler<TxProcessedEventArgs> TransactionProcessed;
            void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext);
        }
    }
}
