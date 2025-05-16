// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Serialization.Rlp;
using Nethermind.State;

namespace Nethermind.Consensus.Tracing;

public class GethStyleTracer(
    IBlockchainProcessor processor,
    IWorldState worldState,
    IReceiptStorage receiptStorage,
    IBlockTree blockTree,
    IBadBlockStore badBlockStore,
    ISpecProvider specProvider,
    ChangeableTransactionProcessorAdapter transactionProcessorAdapter,
    IFileSystem fileSystem,
    IOverridableTxProcessorSource env)
    : IGethStyleTracer
{
    private readonly IBadBlockStore _badBlockStore = badBlockStore ?? throw new ArgumentNullException(nameof(badBlockStore));
    private readonly IBlockTree _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
    private readonly IBlockchainProcessor _processor = processor ?? throw new ArgumentNullException(nameof(processor));
    private readonly IReceiptStorage _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly IOverridableTxProcessorSource _env = env ?? throw new ArgumentNullException(nameof(env));

    public GethLikeTxTrace Trace(Hash256 blockHash, int txIndex, GethTraceOptions options, CancellationToken cancellationToken)
    {
        Block block = _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.None) ?? throw new InvalidOperationException($"No historical block found for {blockHash}");
        if (txIndex > block.Transactions.Length - 1) throw new InvalidOperationException($"Block {blockHash} has only {block.Transactions.Length} transactions and the requested tx index was {txIndex}");

        return TraceImpl(block, block.Transactions[txIndex].Hash, cancellationToken, options);
    }

    public GethLikeTxTrace? Trace(Rlp blockRlp, Hash256 txHash, GethTraceOptions options, CancellationToken cancellationToken)
    {
        return TraceBlockImpl(GetBlockToTrace(blockRlp), options with { TxHash = txHash }, cancellationToken).FirstOrDefault();
    }

    public GethLikeTxTrace? Trace(Block block, Hash256 txHash, GethTraceOptions options, CancellationToken cancellationToken)
    {
        return TraceBlockImpl(block, options with { TxHash = txHash }, cancellationToken).FirstOrDefault();
    }

    public GethLikeTxTrace? Trace(BlockParameter blockParameter, Transaction tx, GethTraceOptions options, CancellationToken cancellationToken)
    {
        Block block = _blockTree.FindBlock(blockParameter) ?? throw new InvalidOperationException($"Cannot find block {blockParameter}");
        tx.Hash ??= tx.CalculateHash();
        block = block.WithReplacedBodyCloned(BlockBody.WithOneTransactionOnly(tx));
        ITransactionProcessorAdapter currentAdapter = transactionProcessorAdapter.CurrentAdapter;
        transactionProcessorAdapter.CurrentAdapter = new TraceTransactionProcessorAdapter(transactionProcessorAdapter.TransactionProcessor);

        try
        {
            return TraceImpl(block, tx.Hash, cancellationToken, options, ProcessingOptions.TraceTransactions);
        }
        finally
        {
            transactionProcessorAdapter.CurrentAdapter = currentAdapter;
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

        return TraceImpl(block, txHash, cancellationToken, traceOptions);
    }

    public GethLikeTxTrace? Trace(long blockNumber, int txIndex, GethTraceOptions options, CancellationToken cancellationToken)
    {
        Block block = _blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.RequireCanonical) ?? throw new InvalidOperationException($"No historical block found for {blockNumber}");
        if (txIndex > block.Transactions.Length - 1) throw new InvalidOperationException($"Block {blockNumber} has only {block.Transactions.Length} transactions and the requested tx index was {txIndex}");

        return TraceImpl(block, block.Transactions[txIndex].Hash, cancellationToken, options);
    }

    public GethLikeTxTrace? Trace(long blockNumber, Transaction tx, GethTraceOptions options, CancellationToken cancellationToken)
    {
        Block block = _blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.RequireCanonical) ?? throw new InvalidOperationException($"No historical block found for {blockNumber}");
        if (tx.Hash is null) throw new InvalidOperationException("Cannot trace transactions without tx hash set.");

        block = block.WithReplacedBodyCloned(BlockBody.WithOneTransactionOnly(tx));
        using IOverridableTxProcessingScope scope = _env.BuildAndOverride(block.Header, options.StateOverrides);
        IBlockTracer<GethLikeTxTrace> blockTracer = CreateOptionsTracer(block.Header, options with { TxHash = tx.Hash }, worldState, specProvider);
        try
        {
            _processor.Process(block, ProcessingOptions.Trace, blockTracer.WithCancellation(cancellationToken), cancellationToken);
            return blockTracer.BuildResult().SingleOrDefault();
        }
        catch
        {
            blockTracer.TryDispose();
            throw;
        }
    }

    public IReadOnlyCollection<GethLikeTxTrace> TraceBlock(BlockParameter blockParameter, GethTraceOptions options, CancellationToken cancellationToken)
    {
        var block = _blockTree.FindBlock(blockParameter);

        return TraceBlockImpl(block, options, cancellationToken);
    }

    public IReadOnlyCollection<GethLikeTxTrace> TraceBlock(Rlp blockRlp, GethTraceOptions options, CancellationToken cancellationToken)
    {
        return TraceBlockImpl(GetBlockToTrace(blockRlp), options, cancellationToken);
    }

    public IReadOnlyCollection<GethLikeTxTrace> TraceBlock(Block block, GethTraceOptions options, CancellationToken cancellationToken)
    {
        return TraceBlockImpl(block, options, cancellationToken);
    }

    public IEnumerable<string> TraceBlockToFile(Hash256 blockHash, GethTraceOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blockHash);
        ArgumentNullException.ThrowIfNull(options);

        var block = _blockTree.FindBlock(blockHash) ?? throw new InvalidOperationException($"No historical block found for {blockHash}");

        if (!block.IsGenesis)
        {
            var parent = _blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.None);

            if (parent?.Hash is null)
                throw new InvalidOperationException("Cannot trace blocks with invalid parents");
        }

        var tracer = new GethLikeBlockFileTracer(block, options, _fileSystem);

        _processor.Process(block, ProcessingOptions.Trace, tracer.WithCancellation(cancellationToken), cancellationToken);

        return tracer.FileNames;
    }

    public IEnumerable<string> TraceBadBlockToFile(Hash256 blockHash, GethTraceOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blockHash);
        ArgumentNullException.ThrowIfNull(options);

        var block = _badBlockStore
                        .GetAll()
                        .FirstOrDefault(b => b.Hash == blockHash)
                    ?? throw new InvalidOperationException($"No historical block found for {blockHash}");

        var tracer = new GethLikeBlockFileTracer(block, options, _fileSystem);

        _processor.Process(block, ProcessingOptions.Trace, tracer.WithCancellation(cancellationToken), cancellationToken);

        return tracer.FileNames;
    }

    private GethLikeTxTrace? TraceImpl(Block block, Hash256? txHash, CancellationToken cancellationToken, GethTraceOptions options,
        ProcessingOptions processingOptions = ProcessingOptions.Trace)
    {
        ArgumentNullException.ThrowIfNull(txHash);

        using IOverridableTxProcessingScope scope = _env.BuildAndOverride(block.Header, options.StateOverrides);
        IBlockTracer<GethLikeTxTrace> tracer = CreateOptionsTracer(block.Header, options with { TxHash = txHash }, worldState, specProvider);

        try
        {
            _processor.Process(block, processingOptions, tracer.WithCancellation(cancellationToken), cancellationToken);
            return tracer.BuildResult().SingleOrDefault();
        }
        catch
        {
            tracer.TryDispose();
            throw;
        }
    }

    public static IBlockTracer<GethLikeTxTrace> CreateOptionsTracer(BlockHeader block, GethTraceOptions options, IWorldState worldState, ISpecProvider specProvider) =>
        options switch
        {
            { Tracer: var t } when GethLikeNativeTracerFactory.IsNativeTracer(t) => new GethLikeBlockNativeTracer(options.TxHash, (b, tx) => GethLikeNativeTracerFactory.CreateTracer(options, b, tx, worldState)),
            { Tracer.Length: > 0 } => new GethLikeBlockJavaScriptTracer(worldState, specProvider.GetSpec(block), options),
            _ => new GethLikeBlockMemoryTracer(options),
        };

    private IReadOnlyCollection<GethLikeTxTrace> TraceBlockImpl(Block? block, GethTraceOptions options, CancellationToken cancellationToken)
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

        using IOverridableTxProcessingScope scope = _env.BuildAndOverride(block.Header, options.StateOverrides);
        IBlockTracer<GethLikeTxTrace> tracer = CreateOptionsTracer(block.Header, options, worldState, specProvider);
        try
        {
            _processor.Process(block, ProcessingOptions.Trace, tracer.WithCancellation(cancellationToken), cancellationToken);
            return new GethLikeTxTraceCollection(tracer.BuildResult());
        }
        catch
        {
            tracer.TryDispose();
            throw;
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
