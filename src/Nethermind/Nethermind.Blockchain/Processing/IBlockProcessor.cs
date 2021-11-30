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

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;

namespace Nethermind.Blockchain.Processing
{
    /// <summary>
    /// Processes a group of blocks
    /// </summary>
    public interface IBlockProcessor
    {
        /// <summary>
        /// Processes a group of blocks starting with a state defined by the <paramref name="newBranchStateRoot"/>.
        /// </summary>
        /// <param name="newBranchStateRoot">Initial state for the processed branch.</param>
        /// <param name="suggestedBlocks">List of blocks to be processed.</param>
        /// <param name="processingOptions">Options to use for processor and transaction processor.</param>
        /// <param name="blockTracer">
        /// Block tracer to use. By default either <see cref="NullBlockTracer"/> or <see cref="BlockReceiptsTracer"/>
        /// </param>
        /// <returns>List of processed blocks.</returns>
        Block[] Process(
            Keccak newBranchStateRoot,
            List<Block> suggestedBlocks,
            ProcessingOptions processingOptions,
            IBlockTracer blockTracer);

        /// <summary>
        /// Fired when a branch is being processed.
        /// </summary>
        event EventHandler<BlocksProcessingEventArgs> BlocksProcessing;
        
        /// <summary>
        /// Fired after a block has been processed.
        /// </summary>
        event EventHandler<BlockProcessedEventArgs> BlockProcessed;
        
        /// <summary>
        /// Fired after a transaction has been processed (even if inside the block).
        /// </summary>
        event EventHandler<TxProcessedEventArgs> TransactionProcessed;
        
        /// <summary>
        /// Strategy used to execute transactions when processing blocks.
        /// </summary>
        /// <remarks>
        /// This strategy can differ depending if:
        /// * block is being processed to be included into canonical chain.
        /// * block is being produced and transactions are being accumulated.
        /// * block is being produced with MEV and bundles can be reverted.
        /// </remarks>
        public interface IBlockTransactionsExecutor
        {
            /// <summary>
            /// Processes transactions from a single block.
            /// </summary>
            /// <remarks>
            /// If its a block production strategy it actually accumulates transactions into a block.
            /// </remarks>
            /// <param name="block">Block to be processed or accumulated.</param>
            /// <param name="processingOptions">Options to use for processor and transaction processor.</param>
            /// <param name="receiptsTracer">Tracer for receipts.</param>
            /// <param name="spec">Current spec with which the processing is being done.</param>
            /// <returns>Receipts created by processing <see cref="block"/> transactions.</returns>
            TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, IReleaseSpec spec);
            
            /// <summary>
            /// Fired after a transaction has been processed.
            /// </summary>
            event EventHandler<TxProcessedEventArgs> TransactionProcessed;
        }
    }
}
