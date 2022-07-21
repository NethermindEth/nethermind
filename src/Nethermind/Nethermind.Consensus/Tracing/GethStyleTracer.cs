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

namespace Nethermind.Consensus.Tracing;

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
        _transactionProcessorAdapter = transactionProcessorAdapter
            ?? throw new ArgumentNullException(nameof(transactionProcessorAdapter));
    }

    public GethLikeTxTrace Trace(Keccak blockHash, int txIndex, GethTraceOptions options, CancellationToken cancellationToken)
    {
        Block block = _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.None);
        if (block == null) throw new InvalidOperationException("Only historical blocks");

        if (txIndex > block.Transactions.Length - 1) throw new InvalidOperationException($"Block {blockHash} has only {block.Transactions.Length} transactions and the requested tx index was {txIndex}");

        return Trace(block, block.Transactions[txIndex].Hash, options, cancellationToken);
    }

    public GethLikeTxTrace? Trace(Rlp block, Keccak txHash, GethTraceOptions options, CancellationToken cancellationToken)
    {
        return TraceBlock(GetBlockToTrace(block), options with { TxHash = txHash }, cancellationToken).FirstOrDefault();
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
            return Trace(block, tx.Hash, options, cancellationToken);
        }
        finally
        {
            _transactionProcessorAdapter.CurrentAdapter = currentAdapter;
        }
    }

    public GethLikeTxTrace? Trace(Keccak txHash, GethTraceOptions options, CancellationToken cancellationToken)
    {
        Keccak? blockHash = _receiptStorage.FindBlockHash(txHash);
        if (blockHash == null)
        {
            return null;
        }

        Block block = _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.RequireCanonical);
        if (block == null)
        {
            return null;
        }

        return Trace(block, txHash, options, cancellationToken);
    }

    public GethLikeTxTrace? Trace(long blockNumber, int txIndex, GethTraceOptions options, CancellationToken cancellationToken)
    {
        Block block = _blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.RequireCanonical);
        if (block == null) throw new InvalidOperationException("Only historical blocks");

        if (txIndex > block.Transactions.Length - 1) throw new InvalidOperationException($"Block {blockNumber} has only {block.Transactions.Length} transactions and the requested tx index was {txIndex}");

        return Trace(block, block.Transactions[txIndex].Hash, options, cancellationToken);
    }

    public GethLikeTxTrace? Trace(long blockNumber, Transaction tx, GethTraceOptions options, CancellationToken cancellationToken)
    {
        Block block = _blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.RequireCanonical);
        if (block == null) throw new InvalidOperationException("Only historical blocks");
        if (tx.Hash == null) throw new InvalidOperationException("Cannot trace transactions without tx hash set.");

        block = block.WithReplacedBodyCloned(BlockBody.WithOneTransactionOnly(tx));
        GethLikeBlockMemoryTracer tracer = new(options with { TxHash = tx.Hash });
        _processor.Process(block, ProcessingOptions.Trace, tracer.WithCancellation(cancellationToken));
        return tracer.BuildResult().SingleOrDefault();
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

    public IEnumerable<string> TraceBlockToFile(Keccak blockHash, GethTraceOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blockHash);
        ArgumentNullException.ThrowIfNull(options);

        var block = _blockTree.FindBlock(blockHash) ?? throw new InvalidOperationException("Only historical blocks");

        if (!block.IsGenesis)
        {
            var parent = _blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.None);

            if (parent?.Hash is null)
                throw new InvalidOperationException("Cannot trace blocks with invalid parents");
        }

        var tracer = new GethLikeBlockFileTracer(block, options);

        _processor.Process(block, ProcessingOptions.Trace, tracer.WithCancellation(cancellationToken));

        return tracer.FileNames;
    }

    private GethLikeTxTrace? Trace(Block block, Keccak txHash, GethTraceOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(txHash);

        var tracer = new GethLikeBlockMemoryTracer(options with { TxHash = txHash });

        _processor.Process(block, ProcessingOptions.Trace, tracer.WithCancellation(cancellationToken));

        return tracer.BuildResult().SingleOrDefault();
    }

    private GethLikeTxTrace[] TraceBlock(Block block, GethTraceOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(block);

        if (!block.IsGenesis)
        {
            BlockHeader? parent = _blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.None);
            if (parent?.Hash is null)
            {
                throw new InvalidOperationException("Cannot trace blocks with invalid parents");
            }

            if (!_blockTree.IsMainChain(parent.Hash)) throw new InvalidOperationException("Cannot trace orphaned blocks");
        }

        GethLikeBlockMemoryTracer tracer = new(options);
        _processor.Process(block, ProcessingOptions.Trace, tracer.WithCancellation(cancellationToken));
        return tracer.BuildResult().ToArray();
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
