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

        public GethLikeTxTrace Trace(Keccak blockHash, int txIndex)
        {
            Block block = _blockTree.FindBlock(blockHash, false);
            if (block == null) throw new InvalidOperationException("Only historical blocks");

            if (txIndex > block.Transactions.Length - 1) throw new InvalidOperationException($"Block {blockHash} has only {block.Transactions.Length} transactions and the requested tx index was {txIndex}");

            return Trace(block, block.Transactions[txIndex].Hash);
        }

        public GethLikeTxTrace Trace(Keccak txHash)
        {
            TransactionReceipt transactionReceipt = _receiptStorage.Get(txHash);
            Block block = _blockTree.FindBlock(transactionReceipt.BlockNumber);
            if (block == null) throw new InvalidOperationException("Only historical blocks");

            return Trace(block, txHash);
        }

        public GethLikeTxTrace Trace(UInt256 blockNumber, int txIndex)
        {
            Block block = _blockTree.FindBlock(blockNumber);
            if (block == null) throw new InvalidOperationException("Only historical blocks");

            if (txIndex > block.Transactions.Length - 1) throw new InvalidOperationException($"Block {blockNumber} has only {block.Transactions.Length} transactions and the requested tx index was {txIndex}");

            return Trace(block, block.Transactions[txIndex].Hash);
        }

        public GethLikeTxTrace Trace(UInt256 blockNumber, Transaction tx)
        {
            Block block = _blockTree.FindBlock(blockNumber);
            if (block == null) throw new InvalidOperationException("Only historical blocks");
            block.Transactions = new[] {tx};
            GethLikeBlockTracer blockTracer = new GethLikeBlockTracer(tx.Hash);
            _processor.Process(block, ProcessingOptions.ForceProcessing | ProcessingOptions.NoValidation | ProcessingOptions.WithRollback | ProcessingOptions.ReadOnlyChain, blockTracer);
            return blockTracer.BuildResult().SingleOrDefault();
        }

        public GethLikeTxTrace[] TraceBlock(Keccak blockHash)
        {
            Block block = _blockTree.FindBlock(blockHash, false);
            return TraceBlock(block);
        }

        public GethLikeTxTrace[] TraceBlock(UInt256 blockNumber)
        {
            Block block = _blockTree.FindBlock(blockNumber);
            return TraceBlock(block);
        }

        public ParityLikeTxTrace ParityTrace(Keccak txHash, ParityTraceTypes parityTraceTypes)
        {
            byte[] traceBytes = _traceDb.Get(txHash); 
            if (traceBytes != null)
            {
                return Rlp.Decode<ParityLikeTxTrace>(traceBytes);
            }
            
            TransactionReceipt transactionReceipt = _receiptStorage.Get(txHash);
            Block block = _blockTree.FindBlock(transactionReceipt.BlockNumber);
            if (block == null) throw new InvalidOperationException("Only historical blocks");

            return ParityTrace(block, txHash, parityTraceTypes);
        }

        public ParityLikeTxTrace[] ParityTraceBlock(UInt256 blockNumber, ParityTraceTypes parityTraceTypes)
        {
            Block block = _blockTree.FindBlock(blockNumber);
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
                    result.AddRange(Rlp.DecodeArray<ParityLikeTxTrace>(new Rlp.DecoderContext(traceBytes), RlpBehaviors.None));
                }
            }

            if (loadedFromDb)
            {
                return result.ToArray();
            }
            
            return ParityTraceBlock(block, parityTraceTypes);
        }

        public ParityLikeTxTrace[] ParityTraceBlock(Keccak blockHash, ParityTraceTypes parityTraceTypes)
        {
            Block block = _blockTree.FindBlock(blockHash, false);
            return ParityTraceBlock(block, parityTraceTypes);
        }

        private GethLikeTxTrace Trace(Block block, Keccak txHash)
        {
            GethLikeBlockTracer listener = new GethLikeBlockTracer(txHash);
            _processor.Process(block, ProcessingOptions.ForceProcessing | ProcessingOptions.WithRollback | ProcessingOptions.ReadOnlyChain, listener);
            return listener.BuildResult().SingleOrDefault();
        }

        private ParityLikeTxTrace ParityTrace(Block block, Keccak txHash, ParityTraceTypes traceTypes)
        {
            ParityLikeBlockTracer listener = new ParityLikeBlockTracer(txHash, traceTypes);
            _processor.Process(block, ProcessingOptions.ForceProcessing | ProcessingOptions.WithRollback | ProcessingOptions.ReadOnlyChain, listener);
            return listener.BuildResult().SingleOrDefault();
        }

        private GethLikeTxTrace[] TraceBlock(Block block)
        {
            if (block == null) throw new InvalidOperationException("Only canonical, historical blocks supported");

            if (block.Number != 0)
            {
                Block parent = _blockTree.FindParent(block);
                if (!_blockTree.IsMainChain(parent.Hash)) throw new InvalidOperationException("Cannot trace orphaned blocks");
            }

            GethLikeBlockTracer listener = new GethLikeBlockTracer();
            _processor.Process(block, ProcessingOptions.ForceProcessing | ProcessingOptions.WithRollback | ProcessingOptions.ReadOnlyChain | ProcessingOptions.NoValidation, listener);
            return listener.BuildResult().ToArray();
        }

        private ParityLikeTxTrace[] ParityTraceBlock(Block block, ParityTraceTypes traceTypes)
        {
            if (block == null) throw new InvalidOperationException("Only canonical, historical blocks supported");

            if (block.Number != 0)
            {
                Block parent = _blockTree.FindParent(block);
                if (!_blockTree.IsMainChain(parent.Hash)) throw new InvalidOperationException("Cannot trace orphaned blocks");
            }

            ParityLikeBlockTracer listener = new ParityLikeBlockTracer(traceTypes);
            _processor.Process(block, ProcessingOptions.ForceProcessing | ProcessingOptions.WithRollback | ProcessingOptions.ReadOnlyChain | ProcessingOptions.NoValidation, listener);
            return listener.BuildResult().ToArray();
        }
    }
}