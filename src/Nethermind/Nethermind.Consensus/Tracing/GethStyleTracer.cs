// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Tracing
{
    public class GethStyleTracer : IGethStyleTracer
    {
        private readonly IBlockTree _blockTree;
        private readonly ChangeableTransactionProcessorAdapter _transactionProcessorAdapter;
        private readonly IBlockchainProcessor _processor;
        private readonly IReceiptStorage _receiptStorage;

        public GethStyleTracer(
            IBlockchainProcessor processor,
            IReceiptStorage receiptStorage,
            IBlockTree blockTree,
            ChangeableTransactionProcessorAdapter transactionProcessorAdapter)
        {
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _transactionProcessorAdapter = transactionProcessorAdapter;
        }

        public GethLikeTxTrace Trace(Keccak blockHash, int txIndex, GethTraceOptions options, CancellationToken cancellationToken)
        {
            Block block = _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.None);
            if (block is null) throw new InvalidOperationException("Only historical blocks");

            if (txIndex > block.Transactions.Length - 1) throw new InvalidOperationException($"Block {blockHash} has only {block.Transactions.Length} transactions and the requested tx index was {txIndex}");

            return Trace(block, block.Transactions[txIndex].Hash, cancellationToken, options);
        }

        public GethLikeTxTrace? Trace(Rlp block, Keccak txHash, GethTraceOptions options, CancellationToken cancellationToken)
        {
            return TraceBlock(GetBlockToTrace(block), options, cancellationToken, txHash).FirstOrDefault();
        }

        public GethLikeTxTrace? Trace(BlockParameter blockParameter, Transaction tx, GethTraceOptions options, CancellationToken cancellationToken)
        {
            Block block = _blockTree.FindBlock(blockParameter);
            if (block is null) throw new InvalidOperationException($"Cannot find block {blockParameter}");
            tx.Hash ??= tx.CalculateHash();
            block = block.WithReplacedBodyCloned(BlockBody.WithOneTransactionOnly(tx));
            ITransactionProcessorAdapter currentAdapter = _transactionProcessorAdapter.CurrentAdapter;
            _transactionProcessorAdapter.CurrentAdapter = new TraceTransactionProcessorAdapter(_transactionProcessorAdapter.TransactionProcessor);

            try
            {
                return Trace(block, tx.Hash, cancellationToken, options);
            }
            finally
            {
                _transactionProcessorAdapter.CurrentAdapter = currentAdapter;
            }
        }

        public GethLikeTxTrace? Trace(Keccak txHash, GethTraceOptions traceOptions, CancellationToken cancellationToken)
        {
            Keccak? blockHash = _receiptStorage.FindBlockHash(txHash);
            if (blockHash is null)
            {
                return null;
            }

            Block block = _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.RequireCanonical);
            if (block is null)
            {
                return null;
            }

            return Trace(block, txHash, cancellationToken, traceOptions);
        }

        public GethLikeTxTrace? Trace(long blockNumber, int txIndex, GethTraceOptions options, CancellationToken cancellationToken)
        {
            Block block = _blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.RequireCanonical);
            if (block is null) throw new InvalidOperationException("Only historical blocks");

            if (txIndex > block.Transactions.Length - 1) throw new InvalidOperationException($"Block {blockNumber} has only {block.Transactions.Length} transactions and the requested tx index was {txIndex}");

            return Trace(block, block.Transactions[txIndex].Hash, cancellationToken, options);
        }

        public GethLikeTxTrace? Trace(long blockNumber, Transaction tx, GethTraceOptions options, CancellationToken cancellationToken)
        {
            Block block = _blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.RequireCanonical);
            if (block is null) throw new InvalidOperationException("Only historical blocks");
            if (tx.Hash is null) throw new InvalidOperationException("Cannot trace transactions without tx hash set.");

            block = block.WithReplacedBodyCloned(BlockBody.WithOneTransactionOnly(tx));
            GethLikeBlockTracer blockTracer = new(tx.Hash, options);
            _processor.Process(block, ProcessingOptions.Trace, blockTracer.WithCancellation(cancellationToken));
            return blockTracer.BuildResult().SingleOrDefault();
        }
        public GethLikeTxTrace[] TraceBlock(BlockParameter blockParameter, GethTraceOptions options, CancellationToken cancellationToken)
        {
            var block = _blockTree.FindBlock(blockParameter);

            return TraceBlock(block, options, cancellationToken);
        }

        public GethLikeTxTrace[] TraceBlock(Rlp blockRlp, GethTraceOptions options, CancellationToken cancellationToken)
        {
            return TraceBlock(GetBlockToTrace(blockRlp), options, cancellationToken);
        }

        private GethLikeTxTrace? Trace(Block block, Keccak? txHash, CancellationToken cancellationToken, GethTraceOptions options)
        {
            if (txHash is null) throw new InvalidOperationException("Cannot trace transactions without tx hash set.");

            GethLikeBlockTracer listener = new(txHash, options);
            _processor.Process(block, ProcessingOptions.Trace, listener.WithCancellation(cancellationToken));
            return listener.BuildResult().SingleOrDefault();
        }

        private GethLikeTxTrace[] TraceBlock(Block? block, GethTraceOptions options, CancellationToken cancellationToken, Keccak? txHash = null)
        {
            if (block is null) throw new InvalidOperationException("Only canonical, historical blocks supported");

            if (!block.IsGenesis)
            {
                BlockHeader? parent = _blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.None);
                if (parent?.Hash is null)
                {
                    throw new InvalidOperationException("Cannot trace blocks with invalid parents");
                }

                if (!_blockTree.IsMainChain(parent.Hash)) throw new InvalidOperationException("Cannot trace orphaned blocks");
            }

            GethLikeBlockTracer listener = txHash is null ? new GethLikeBlockTracer(options) : new GethLikeBlockTracer(txHash, options);
            _processor.Process(block, ProcessingOptions.Trace, listener.WithCancellation(cancellationToken));
            return listener.BuildResult().ToArray();
        }

        private static Block GetBlockToTrace(Rlp blockRlp)
        {
            Block block = Rlp.Decode<Block>(blockRlp);
            if (block.TotalDifficulty is null)
            {
                block.Header.TotalDifficulty = 1;
            }

            return block;
        }
    }
}
