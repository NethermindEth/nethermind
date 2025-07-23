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
using Nethermind.Evm.State;
using Nethermind.State.OverridableEnv;
using Nethermind.Evm.Tracing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Tracing;

public class GethStyleTracer(
    IReceiptStorage receiptStorage,
    IBlockTree blockTree,
    IBadBlockStore badBlockStore,
    ISpecProvider specProvider,
    ChangeableTransactionProcessorAdapter transactionProcessorAdapter,
    IFileSystem fileSystem,
    IOverridableEnv<GethStyleTracer.BlockProcessingComponents> blockProcessingEnv
) : IGethStyleTracer
{
    public GethLikeTxTrace Trace(Hash256 blockHash, int txIndex, GethTraceOptions options, CancellationToken cancellationToken)
    {
        Block block = blockTree.FindBlock(blockHash, BlockTreeLookupOptions.None) ?? throw new InvalidOperationException($"No historical block found for {blockHash}");
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
        Block block = blockTree.FindBlock(blockParameter) ?? throw new InvalidOperationException($"Cannot find block {blockParameter}");
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
        Hash256? blockHash = receiptStorage.FindBlockHash(txHash);
        if (blockHash is null)
        {
            return null;
        }

        Block block = blockTree.FindBlock(blockHash, BlockTreeLookupOptions.RequireCanonical);
        if (block is null)
        {
            return null;
        }

        return TraceImpl(block, txHash, cancellationToken, traceOptions);
    }

    public GethLikeTxTrace? Trace(long blockNumber, int txIndex, GethTraceOptions options, CancellationToken cancellationToken)
    {
        Block block = blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.RequireCanonical) ?? throw new InvalidOperationException($"No historical block found for {blockNumber}");
        if (txIndex > block.Transactions.Length - 1) throw new InvalidOperationException($"Block {blockNumber} has only {block.Transactions.Length} transactions and the requested tx index was {txIndex}");

        return TraceImpl(block, block.Transactions[txIndex].Hash, cancellationToken, options);
    }

    public GethLikeTxTrace? Trace(long blockNumber, Transaction tx, GethTraceOptions options, CancellationToken cancellationToken)
    {
        Block block = blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.RequireCanonical) ?? throw new InvalidOperationException($"No historical block found for {blockNumber}");
        if (tx.Hash is null) throw new InvalidOperationException("Cannot trace transactions without tx hash set.");

        block = block.WithReplacedBodyCloned(BlockBody.WithOneTransactionOnly(tx));
        using var scope = blockProcessingEnv.BuildAndOverride(block.Header, options.StateOverrides);
        IBlockTracer<GethLikeTxTrace> blockTracer = CreateOptionsTracer(block.Header, options with { TxHash = tx.Hash }, scope.Component.WorldState, specProvider);
        try
        {
            scope.Component.BlockchainProcessor.Process(block, ProcessingOptions.Trace, blockTracer.WithCancellation(cancellationToken), cancellationToken);
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
        var block = blockTree.FindBlock(blockParameter);

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

        var block = blockTree.FindBlock(blockHash) ?? throw new InvalidOperationException($"No historical block found for {blockHash}");
        var parent = FindParent(block);

        using var scope = blockProcessingEnv.BuildAndOverride(parent, options.StateOverrides);
        var tracer = new GethLikeBlockFileTracer(block, options, fileSystem);
        scope.Component.BlockchainProcessor.Process(block, ProcessingOptions.Trace, tracer.WithCancellation(cancellationToken), cancellationToken);

        return tracer.FileNames;
    }

    public IEnumerable<string> TraceBadBlockToFile(Hash256 blockHash, GethTraceOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blockHash);
        ArgumentNullException.ThrowIfNull(options);

        var block = badBlockStore
                        .GetAll()
                        .FirstOrDefault(b => b.Hash == blockHash)
                    ?? throw new InvalidOperationException($"No historical block found for {blockHash}");
        var parent = FindParent(block);
        using var scope = blockProcessingEnv.BuildAndOverride(parent, options.StateOverrides);
        var tracer = new GethLikeBlockFileTracer(block, options, fileSystem);
        scope.Component.BlockchainProcessor.Process(block, ProcessingOptions.Trace, tracer.WithCancellation(cancellationToken), cancellationToken);

        return tracer.FileNames;
    }

    private GethLikeTxTrace? TraceImpl(Block block, Hash256? txHash, CancellationToken cancellationToken, GethTraceOptions options,
        ProcessingOptions processingOptions = ProcessingOptions.Trace)
    {
        ArgumentNullException.ThrowIfNull(txHash);

        // Previously, when the processing options is not `TraceTransaction`, the base block is the parent of the block
        // which is set by the `BranchProcessor`, which mean the state override probably does not take affect.
        // However, when it is `TraceTransactioon`, it apply `ForceSameBlock` to `BlockchainProcessor` which will send the same
        // block as the baseBlock, which is important as the stateroot of the baseblock is modified in `BuildAndOverride`.
        //
        // Wild stuff!
        BlockHeader baseBlockHeader = block.Header;
        if ((processingOptions & ProcessingOptions.ForceSameBlock) == 0)
        {
            baseBlockHeader = FindParent(block);
        }

        using var scope = blockProcessingEnv.BuildAndOverride(baseBlockHeader, options.StateOverrides);
        IBlockTracer<GethLikeTxTrace> tracer = CreateOptionsTracer(block.Header, options with { TxHash = txHash }, scope.Component.WorldState, specProvider);

        try
        {
            scope.Component.BlockchainProcessor.Process(block, processingOptions, tracer.WithCancellation(cancellationToken), cancellationToken);
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

        var parent = FindParent(block);
        using var scope = blockProcessingEnv.BuildAndOverride(parent, options.StateOverrides);

        IBlockTracer<GethLikeTxTrace> tracer = CreateOptionsTracer(block.Header, options, scope.Component.WorldState, specProvider);
        try
        {
            scope.Component.BlockchainProcessor.Process(block, ProcessingOptions.Trace, tracer.WithCancellation(cancellationToken), cancellationToken);
            return new GethLikeTxTraceCollection(tracer.BuildResult());
        }
        catch
        {
            tracer.TryDispose();
            throw;
        }
    }

    private BlockHeader? FindParent(Block block)
    {
        BlockHeader? parent = null;

        if (!block.IsGenesis)
        {
            parent = blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.None);

            if (parent?.Hash is null)
                throw new InvalidOperationException("Cannot trace blocks with invalid parents");
        }

        return parent;
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

    public record BlockProcessingComponents(IWorldState WorldState, BlockchainProcessorFacade BlockchainProcessor);
}
