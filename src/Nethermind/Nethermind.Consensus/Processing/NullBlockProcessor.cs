// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Processing
{
    public class NullBlockProcessor : IBlockProcessor
    {
        private NullBlockProcessor() { }

        public static IBlockProcessor Instance { get; } = new NullBlockProcessor();

        public Block[] Process(BlockHeader? baseBlock, IReadOnlyList<Block> suggestedBlocks, ProcessingOptions processingOptions, IBlockTracer blockTracer, CancellationToken token) =>
            suggestedBlocks.ToArray();

        public bool ValidateInclusionList(Block suggestedBlock, Block block, ProcessingOptions options)
        {
            throw new NotImplementedException();
        }

        // public event EventHandler<BlocksProcessingEventArgs> BlocksProcessing
        public (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options,
            IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token)
        {
            return (suggestedBlock, []);
        }

        public event EventHandler<TxProcessedEventArgs> TransactionProcessed
        {
            add { }
            remove { }
        }
    }
}
