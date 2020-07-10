﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Linq;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Tracing
{
    public class GethStyleTracer : IGethStyleTracer
    {
        private readonly IBlockTree _blockTree;
        private readonly IBlockchainProcessor _processor;
        private readonly IReceiptStorage _receiptStorage;
        private readonly CancellationToken _cancellationToken; 

        public GethStyleTracer(IBlockchainProcessor processor, IReceiptStorage receiptStorage, IBlockTree blockTree, CancellationToken cancellationToken = default(CancellationToken))
        {
            _cancellationToken = cancellationToken;
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        }

        public GethLikeTxTrace Trace(Keccak blockHash, int txIndex, GethTraceOptions options)
        {
            Block block = _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.None);
            if (block == null) throw new InvalidOperationException("Only historical blocks");

            if (txIndex > block.Transactions.Length - 1) throw new InvalidOperationException($"Block {blockHash} has only {block.Transactions.Length} transactions and the requested tx index was {txIndex}");

            return Trace(block, block.Transactions[txIndex].Hash, options);
        }

        public GethLikeTxTrace Trace(Rlp block, Keccak txHash, GethTraceOptions options)
        {
            return TraceBlock(GetBlockToTrace(block), options, txHash).FirstOrDefault();
        }

        public GethLikeTxTrace Trace(Keccak txHash, GethTraceOptions traceOptions)
        {
            Keccak blockHash = _receiptStorage.FindBlockHash(txHash);
            if (blockHash == null)
            {
                return null;
            }

            Block block = _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.RequireCanonical);
            if (block == null)
            {
                return null;
            }

            return Trace(block, txHash, traceOptions);
        }

        public GethLikeTxTrace Trace(long blockNumber, int txIndex, GethTraceOptions options)
        {
            Block block = _blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.RequireCanonical);
            if (block == null) throw new InvalidOperationException("Only historical blocks");

            if (txIndex > block.Transactions.Length - 1) throw new InvalidOperationException($"Block {blockNumber} has only {block.Transactions.Length} transactions and the requested tx index was {txIndex}");

            return Trace(block, block.Transactions[txIndex].Hash, options);
        }

        public GethLikeTxTrace Trace(long blockNumber, Transaction tx, GethTraceOptions options)
        {
            Block block = _blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.RequireCanonical);
            if (block == null) throw new InvalidOperationException("Only historical blocks");
            block.Body = new BlockBody(new[] {tx}, new BlockHeader[] { });
            GethLikeBlockTracer blockTracer = new GethLikeBlockTracer(tx.Hash, options, _cancellationToken);
            _processor.Process(block, ProcessingOptions.Trace, blockTracer);
            return blockTracer.BuildResult().SingleOrDefault();
        }

        public GethLikeTxTrace[] TraceBlock(Keccak blockHash, GethTraceOptions options)
        {
            Block block = _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.None);
            return TraceBlock(block, options);
        }

        public GethLikeTxTrace[] TraceBlock(long blockNumber, GethTraceOptions options)
        {
            Block block = _blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.RequireCanonical);
            return TraceBlock(block, options);
        }

        public GethLikeTxTrace[] TraceBlock(Rlp blockRlp, GethTraceOptions options)
        {
            return TraceBlock(GetBlockToTrace(blockRlp), options);
        }

        private GethLikeTxTrace Trace(Block block, Keccak txHash, GethTraceOptions options)
        {
            GethLikeBlockTracer listener = new GethLikeBlockTracer(txHash, options, _cancellationToken);
            _processor.Process(block, ProcessingOptions.Trace, listener);
            return listener.BuildResult().SingleOrDefault();
        }

        private GethLikeTxTrace[] TraceBlock(Block block, GethTraceOptions options, Keccak txHash = null)
        {
            if (block == null) throw new InvalidOperationException("Only canonical, historical blocks supported");

            if (block.Number != 0)
            {
                BlockHeader parent = _blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.None);
                if (!_blockTree.IsMainChain(parent.Hash)) throw new InvalidOperationException("Cannot trace orphaned blocks");
            }

            GethLikeBlockTracer listener = txHash == null ? new GethLikeBlockTracer(options, _cancellationToken) : new GethLikeBlockTracer(txHash, options, _cancellationToken);
            _processor.Process(block, ProcessingOptions.Trace, listener);
            return listener.BuildResult().ToArray();
        }

        private static Block GetBlockToTrace(Rlp blockRlp)
        {
            Block block = Rlp.Decode<Block>(blockRlp);
            if (block.TotalDifficulty == null)
            {
                block.Header.TotalDifficulty = 1;
            }

            return block;
        }
    }
}