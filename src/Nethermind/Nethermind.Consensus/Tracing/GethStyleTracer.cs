// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Serialization.Rlp;
using Nethermind.State;

namespace Nethermind.Consensus.Tracing;

public class GethStyleTracer : IGethStyleTracer
{
    private readonly IBlockTree _blockTree;
    private readonly ISpecProvider _specProvider;
    private readonly ChangeableTransactionProcessorAdapter _transactionProcessorAdapter;
    private readonly IBlockchainProcessor _processor;
    private readonly IWorldState _worldState;
    private readonly IReceiptStorage _receiptStorage;
    private readonly IFileSystem _fileSystem;

    public GethStyleTracer(IBlockchainProcessor processor,
        IWorldState worldState,
        IReceiptStorage receiptStorage,
        IBlockTree blockTree,
        ISpecProvider specProvider,
        ChangeableTransactionProcessorAdapter transactionProcessorAdapter,
        IFileSystem fileSystem)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _worldState = worldState;
        _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _specProvider = specProvider;
        _transactionProcessorAdapter = transactionProcessorAdapter;
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public GethLikeTxTrace Trace(Hash256 blockHash, int txIndex, GethTraceOptions options, CancellationToken cancellationToken)
    {
        Block block = _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.None);
        if (block is null) throw new InvalidOperationException("Only historical blocks");

        if (txIndex > block.Transactions.Length - 1) throw new InvalidOperationException($"Block {blockHash} has only {block.Transactions.Length} transactions and the requested tx index was {txIndex}");

        return Trace(block, block.Transactions[txIndex].Hash, cancellationToken, options);
    }

    public GethLikeTxTrace? Trace(Rlp block, Hash256 txHash, GethTraceOptions options, CancellationToken cancellationToken)
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
            return Trace(block, tx.Hash, cancellationToken, options);
        }
        finally
        {
            _transactionProcessorAdapter.CurrentAdapter = currentAdapter;
        }
    }

    public GethLikeTxTrace? Trace(Hash256 txHash, GethTraceOptions traceOptions, CancellationToken cancellationToken)
    {
        Hash256? blockHash = _receiptStorage.FindBlockHash(txHash);
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
        IBlockTracer<GethLikeTxTrace> blockTracer = CreateOptionsTracer(block.Header, options with { TxHash = tx.Hash });
        try
        {
            _processor.Process(block, ProcessingOptions.Trace, blockTracer.WithCancellation(cancellationToken));
            return blockTracer.BuildResult().SingleOrDefault();
        }
        finally
        {
            blockTracer.TryDispose();
        }
    }

    public IReadOnlyCollection<GethLikeTxTrace> TraceBlock(BlockParameter blockParameter, GethTraceOptions options, CancellationToken cancellationToken)
    {
        var block = _blockTree.FindBlock(blockParameter);

        return TraceBlock(block, options, cancellationToken);
    }

    public IReadOnlyCollection<GethLikeTxTrace> TraceBlock(Rlp blockRlp, GethTraceOptions options, CancellationToken cancellationToken)
    {
        return TraceBlock(GetBlockToTrace(blockRlp), options, cancellationToken);
    }

    public IEnumerable<string> TraceBlockToFile(Hash256 blockHash, GethTraceOptions options, CancellationToken cancellationToken)
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

        var tracer = new GethLikeBlockFileTracer(block, options, _fileSystem);

        _processor.Process(block, ProcessingOptions.Trace, tracer.WithCancellation(cancellationToken));

        return tracer.FileNames;
    }

    private GethLikeTxTrace? Trace(Block block, Hash256? txHash, CancellationToken cancellationToken, GethTraceOptions options)
    {
        ArgumentNullException.ThrowIfNull(txHash);

        IBlockTracer<GethLikeTxTrace> tracer = CreateOptionsTracer(block.Header, options with { TxHash = txHash });

        try
        {
            _processor.Process(block, ProcessingOptions.Trace, tracer.WithCancellation(cancellationToken));
            return tracer.BuildResult().SingleOrDefault();
        }
        finally
        {
            tracer.TryDispose();
        }
    }

    private IBlockTracer<GethLikeTxTrace> CreateOptionsTracer(BlockHeader block, GethTraceOptions options) =>
        options switch
        {
            { Tracer: var t } when GethLikeNativeTracerFactory.IsNativeTracer(t) => new GethLikeBlockNativeTracer(options.TxHash, (b, tx) => GethLikeNativeTracerFactory.CreateTracer(options, b, tx, _worldState)),
            { Tracer.Length: > 0 } => new GethLikeBlockJavaScriptTracer(_worldState, _specProvider.GetSpec(block), options),
            _ => new GethLikeBlockMemoryTracer(options),
        };

    private IReadOnlyCollection<GethLikeTxTrace> TraceBlock(Block? block, GethTraceOptions options, CancellationToken cancellationToken)
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

        IBlockTracer<GethLikeTxTrace> tracer = CreateOptionsTracer(block.Header, options);
        try
        {
            _processor.Process(block, ProcessingOptions.Trace, tracer.WithCancellation(cancellationToken));
            return tracer.BuildResult();
        }
        finally
        {
            tracer.TryDispose();
        }
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
