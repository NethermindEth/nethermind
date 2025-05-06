// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Processing
{
    public interface IBlockProcessor
    {
        /// <summary>
        /// Processes a group of blocks starting with a state defined by the <paramref name="newBranchStateRoot"/>.
        /// </summary>
        /// <param name="newBranchStateRoot">Initial state for the processed branch.</param>
        /// <param name="suggestedBlocks">List of blocks to be processed.</param>
        /// <param name="processingOptions">Options to use for processor and transaction processor.</param>
        /// <param name="blockTracer">Block tracer to use. By default either <see cref="NullBlockTracer"/> or <see cref="BlockReceiptsTracer"/></param>
        /// <returns>List of processed blocks.</returns>
        Block[] Process(
            Hash256 newBranchStateRoot,
            IReadOnlyList<Block> suggestedBlocks,
            ProcessingOptions processingOptions,
            IBlockTracer blockTracer,
            CancellationToken token = default);

        /// <summary>
        /// Fired when a branch is being processed.
        /// </summary>
        event EventHandler<BlocksProcessingEventArgs> BlocksProcessing;

        /// <summary>
        /// Fired when a block is being processed.
        /// </summary>
        event EventHandler<BlockEventArgs> BlockProcessing;

        /// <summary>
        /// Fired after a block has been processed.
        /// </summary>
        event EventHandler<BlockProcessedEventArgs> BlockProcessed;

        /// <summary>
        /// Fired after a transaction has been processed (even if inside the block).
        /// </summary>
        event EventHandler<TxProcessedEventArgs> TransactionProcessed;

        public interface IBlockTransactionsExecutor
        {
            TxReceipt[] ProcessTransactions(Block block, in BlockExecutionContext blkCtx, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, IReleaseSpec spec, CancellationToken token = default);
            event EventHandler<TxProcessedEventArgs> TransactionProcessed;
        }
    }
}
