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
        /// <summary>
        /// Raised after all transactions in a block have been executed,
        /// before blooms, receipts root, and state root are computed.
        /// Subscribers can use this to cancel background work (e.g. prewarmer)
        /// so the thread pool is free for the parallel post-tx computations.
        /// </summary>
        event Action? TransactionsExecuted;

        /// <summary>Processes a block, or a transaction prefix in a compatible read-only tracing environment.</summary>
        /// <remarks>
        /// A transaction-only trace may return fewer receipts than transactions. Such receipts have no computed bloom,
        /// and the returned block has unfinalized roots, bloom and prefix-only gas totals. Consumers must not persist
        /// these results or assume full-block receipt indexing. The suggested block's execution artifacts are not
        /// updated by tracing. Ordinary block processing retains the complete, finalized result contract.
        /// </remarks>
        public (Block Block, TxReceipt[] Receipts) ProcessOne(
            Block suggestedBlock,
            ProcessingOptions options,
            IBlockTracer blockTracer,
            IReleaseSpec spec,
            CancellationToken token = default);

        public interface IBlockTransactionsExecutor
        {
            TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token = default);
            void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext);

            // Optional per-tx timing instrumentation. Default no-op implementations let executors that
            // don't capture per-tx timing (block production, simulation, invalid-tx) ignore these.
            void SetupTxTimingMetrics(Block block) { }
            long StartTxTimer() => 0;
            void StopTxTimer(int i, long txStart) { }
        }
    }
}
