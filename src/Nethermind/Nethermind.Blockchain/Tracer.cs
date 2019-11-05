/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
using Nethermind.Store;

namespace Nethermind.Blockchain
{
    public class Tracer : ITracer
    {
        private readonly IBlockTree _blockTree;
        private readonly IDb _traceDb;
        private readonly IBlockchainProcessor _processor;
        private readonly IReceiptStorage _receiptStorage;

        public Tracer(IBlockchainProcessor processor, IReceiptStorage receiptStorage, IBlockTree blockTree, IDb traceDb)
        {
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _traceDb = traceDb ?? throw new ArgumentNullException(nameof(traceDb));
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
            TxReceipt txReceipt = _receiptStorage.Find(txHash);
            if (txReceipt == null)
            {
                return null;
            }

            Block block = _blockTree.FindBlock(txReceipt.BlockNumber, BlockTreeLookupOptions.RequireCanonical);
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
            GethLikeBlockTracer blockTracer = new GethLikeBlockTracer(tx.Hash, options);
            _processor.Process(block, ProcessingOptions.ForceProcessing | ProcessingOptions.NoValidation | ProcessingOptions.WithRollback | ProcessingOptions.ReadOnlyChain, blockTracer);
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

        public ParityLikeTxTrace ParityTrace(Keccak txHash, ParityTraceTypes traceTypes)
        {
            byte[] traceBytes = _traceDb.Get(txHash);
            if (traceBytes != null)
            {
                return Rlp.Decode<ParityLikeTxTrace>(traceBytes);
            }

            TxReceipt txReceipt = _receiptStorage.Find(txHash);
            Block block = _blockTree.FindBlock(txReceipt.BlockNumber, BlockTreeLookupOptions.RequireCanonical);
            if (block == null) throw new InvalidOperationException("Only historical blocks");

            return ParityTrace(block, txHash, traceTypes);
        }

        public ParityLikeTxTrace[] ParityTraceBlock(long blockNumber, ParityTraceTypes traceTypes)
        {
            Block block = _blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.RequireCanonical);
            bool loadedFromDb = true;

            List<ParityLikeTxTrace> result = new List<ParityLikeTxTrace>();
            for (int i = 0; i < block.Transactions.Length; i++)
            {
                byte[] traceBytes = _traceDb.Get(block.Transactions[i].Hash);
                if (traceBytes != null)
                {
                    result.Add(Rlp.Decode<ParityLikeTxTrace>(traceBytes));
                }
                else
                {
                    loadedFromDb = false;
                    break;
                }
            }

            if (loadedFromDb)
            {
                byte[] traceBytes = _traceDb.Get(block.Hash);
                if (traceBytes != null)
                {
                    result.AddRange(Rlp.DecodeArray<ParityLikeTxTrace>(new RlpStream(traceBytes), RlpBehaviors.None));
                }
            }

            if (loadedFromDb)
            {
                return result.ToArray();
            }

            return ParityTraceBlock(block, traceTypes);
        }

        private TransactionDecoder _transactionDecoder = new TransactionDecoder();

        public ParityLikeTxTrace ParityTraceRawTransaction(byte[] txRlp, ParityTraceTypes traceTypes)
        {
            BlockHeader headBlockHeader = _blockTree.Head;
            BlockHeader traceHeader = new BlockHeader(
                headBlockHeader.Hash,
                Keccak.OfAnEmptySequenceRlp,
                Address.Zero,
                headBlockHeader.Difficulty,
                headBlockHeader.Number + 1,
                headBlockHeader.GasLimit,
                headBlockHeader.Timestamp + 1,
                headBlockHeader.ExtraData);

            Transaction tx = _transactionDecoder.Decode(new RlpStream(txRlp));
            Block block = new Block(traceHeader, new[] {tx}, Enumerable.Empty<BlockHeader>());
            block.Author = Address.Zero;
            block.TotalDifficulty = headBlockHeader.TotalDifficulty + traceHeader.Difficulty;

            return ParityTraceBlock(block, traceTypes)[0];
        }

        public ParityLikeTxTrace[] ParityTraceBlock(Keccak blockHash, ParityTraceTypes traceTypes)
        {
            Block block = _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.None);
            return ParityTraceBlock(block, traceTypes);
        }

        private GethLikeTxTrace Trace(Block block, Keccak txHash, GethTraceOptions options)
        {
            GethLikeBlockTracer listener = new GethLikeBlockTracer(txHash, options);
            _processor.Process(block, ProcessingOptions.ForceProcessing | ProcessingOptions.WithRollback | ProcessingOptions.ReadOnlyChain, listener);
            return listener.BuildResult().SingleOrDefault();
        }

        private ParityLikeTxTrace ParityTrace(Block block, Keccak txHash, ParityTraceTypes traceTypes)
        {
            ParityLikeBlockTracer listener = new ParityLikeBlockTracer(txHash, traceTypes);
            _processor.Process(block, ProcessingOptions.ForceProcessing | ProcessingOptions.WithRollback | ProcessingOptions.ReadOnlyChain, listener);
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

            GethLikeBlockTracer listener = txHash == null ? new GethLikeBlockTracer(options) : new GethLikeBlockTracer(txHash, options);
            _processor.Process(block, ProcessingOptions.ForceProcessing | ProcessingOptions.WithRollback | ProcessingOptions.ReadOnlyChain | ProcessingOptions.NoValidation, listener);
            return listener.BuildResult().ToArray();
        }

        private ParityLikeTxTrace[] ParityTraceBlock(Block block, ParityTraceTypes traceTypes)
        {
            if (block == null) throw new InvalidOperationException("Only canonical, historical blocks supported");

            if (block.Number != 0)
            {
                BlockHeader parent = _blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.None);
                if (!_blockTree.IsMainChain(parent.Hash)) throw new InvalidOperationException("Cannot trace orphaned blocks");
            }

            ParityLikeBlockTracer listener = new ParityLikeBlockTracer(traceTypes);
            _processor.Process(block, ProcessingOptions.ForceProcessing | ProcessingOptions.WithRollback | ProcessingOptions.ReadOnlyChain | ProcessingOptions.NoValidation, listener);
            return listener.BuildResult().ToArray();
        }

        private static Block GetBlockToTrace(Rlp blockRlp)
        {
            Block block = Rlp.Decode<Block>(blockRlp);
            if (block.TotalDifficulty == null)
            {
                block.TotalDifficulty = 1;
            }

            return block;
        }
    }
}