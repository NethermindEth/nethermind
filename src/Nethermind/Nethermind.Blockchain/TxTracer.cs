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
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.TransactionPools;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;

namespace Nethermind.Blockchain
{
    public class TxTracer : ITxTracer
    {
        private readonly IBlockTree _blockTree;
        private readonly IBlockchainProcessor _processor;
        private readonly IReceiptStorage _receiptStorage;

        public TxTracer(IBlockchainProcessor processor, IReceiptStorage receiptStorage, IBlockTree blockTree)
        {
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        }

        public TransactionTrace Trace(Keccak blockHash, int txIndex)
        {
            Block block = _blockTree.FindBlock(blockHash, false);
            if (block == null) throw new InvalidOperationException("Only historical blocks");

            if (txIndex > block.Transactions.Length - 1) throw new InvalidOperationException($"Block {blockHash} has only {block.Transactions.Length} transactions and the requested tx index was {txIndex}");

            return Trace(block, block.Transactions[txIndex].Hash);
        }

        public TransactionTrace Trace(Keccak txHash)
        {
            TransactionReceipt receipt = _receiptStorage.Get(txHash);
            Block block = _blockTree.FindBlock(receipt.BlockNumber);
            if (block == null) throw new InvalidOperationException("Only historical blocks");

            return Trace(block, txHash);
        }

        public TransactionTrace Trace(UInt256 blockNumber, int txIndex)
        {
            Block block = _blockTree.FindBlock(blockNumber);
            if (block == null) throw new InvalidOperationException("Only historical blocks");

            if (txIndex > block.Transactions.Length - 1) throw new InvalidOperationException($"Block {blockNumber} has only {block.Transactions.Length} transactions and the requested tx index was {txIndex}");

            return Trace(block, block.Transactions[txIndex].Hash);
        }

        public TransactionTrace Trace(UInt256 blockNumber, Transaction tx)
        {
            Block block = _blockTree.FindBlock(blockNumber);
            if (block == null) throw new InvalidOperationException("Only historical blocks");
            block.Transactions = new[] {tx};
            TraceListener listener = new TraceListener(tx.Hash);
            _processor.Process(block, ProcessingOptions.ForceProcessing | ProcessingOptions.NoValidation | ProcessingOptions.WithRollback | ProcessingOptions.ReadOnlyChain, listener);
            return listener.Trace;
        }

        public BlockTrace TraceBlock(Keccak blockHash)
        {
            Block block = _blockTree.FindBlock(blockHash, false);
            return TraceBlock(block);
        }

        public BlockTrace TraceBlock(UInt256 blockNumber)
        {
            Block block = _blockTree.FindBlock(blockNumber);
            return TraceBlock(block);
        }

        private TransactionTrace Trace(Block block, Keccak txHash)
        {
            TraceListener listener = new TraceListener(txHash);
            _processor.Process(block, ProcessingOptions.ForceProcessing | ProcessingOptions.WithRollback | ProcessingOptions.ReadOnlyChain, listener);
            return listener.Trace;
        }

        private BlockTrace TraceBlock(Block block)
        {
            if (block == null) throw new InvalidOperationException("Only canonical, historical blocks supported");

            if (block.Number != 0)
            {
                Block parent = _blockTree.FindParent(block);
                if (!_blockTree.IsMainChain(parent.Hash)) throw new InvalidOperationException("Cannot trace orphaned blocks");
            }

            BlockTraceListener listener = new BlockTraceListener(block);
            _processor.Process(block, ProcessingOptions.ForceProcessing | ProcessingOptions.WithRollback | ProcessingOptions.ReadOnlyChain | ProcessingOptions.NoValidation, listener);
            return listener.BlockTrace;
        }
    }
}