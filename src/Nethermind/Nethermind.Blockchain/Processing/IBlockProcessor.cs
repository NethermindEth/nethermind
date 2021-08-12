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
        
        public interface IBlockTransactionsExecutor
        {
            TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, IReleaseSpec spec);
            event EventHandler<TxProcessedEventArgs> TransactionProcessed;
        }
    }
}
