// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Processing
{
    public class NullBlockProcessor : IBlockProcessor
    {
        private NullBlockProcessor() { }

        public static IBlockProcessor Instance { get; } = new NullBlockProcessor();

        public Block[] Process(Hash256 newBranchStateRoot, IReadOnlyList<Block> suggestedBlocks, ProcessingOptions processingOptions, IBlockTracer blockTracer, CancellationToken token) =>
            suggestedBlocks.ToArray();

        public event EventHandler<BlocksProcessingEventArgs> BlocksProcessing
        {
            add { }
            remove { }
        }

        public event EventHandler<BlockEventArgs>? BlockProcessing
        {
            add { }
            remove { }
        }

        public event EventHandler<BlockProcessedEventArgs> BlockProcessed
        {
            add { }
            remove { }
        }

        public event EventHandler<TxProcessedEventArgs> TransactionProcessed
        {
            add { }
            remove { }
        }
    }
}
