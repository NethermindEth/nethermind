// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.State.OverridableEnv;
using Nethermind.Evm.Tracing;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Tracing;

public class GethStyleTracer(
    IReceiptStorage receiptStorage,
    IBlockTree blockTree,
    IBadBlockStore badBlockStore,
    ISpecProvider specProvider,
    ChangeableTransactionProcessorAdapter transactionProcessorAdapter,
    IFileSystem fileSystem,
    IOverridableEnv<GethStyleTracer.BlockProcessingComponents> blockProcessingEnv,
    IPrefixStateSeedSource prefixSeeds,
    IParallelBlockTracer? parallelTracer = null
) : IGethStyleTracer
{
    public GethLikeTxTrace? Trace(Hash256 blockHash, int txIndex, GethTraceOptions options, CancellationToken cancellationToken, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null)
    {
        Block block = blockTree.FindBlock(blockHash, BlockTreeLookupOptions.None) ?? throw new InvalidOperationException($"No historical block found for {blockHash}");
        if ((uint)txIndex >= (uint)block.Transactions.Length) throw new InvalidOperationException($"Block {blockHash} has only {block.Transactions.Length} transactions and the requested tx index was {txIndex}");

        return TraceImpl(block, block.Transactions[txIndex].Hash, cancellationToken, options, writer: writer, pipeWriter: pipeWriter);
    }

    public GethLikeTxTrace? Trace(Rlp blockRlp, Hash256 txHash, GethTraceOptions options, CancellationToken cancellationToken, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null) =>
        TraceImpl(GetBlockToTrace(blockRlp), txHash, cancellationToken, options, writer: writer, pipeWriter: pipeWriter, allowIndexed: false);

    public GethLikeTxTrace? Trace(Block block, Hash256 txHash, GethTraceOptions options, CancellationToken cancellationToken, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null) =>
        TraceImpl(block, txHash, cancellationToken, options, writer: writer, pipeWriter: pipeWriter, allowIndexed: false);

    public GethLikeTxTrace? Trace(BlockParameter blockParameter, Transaction tx, GethTraceOptions options, CancellationToken cancellationToken, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null)
    {
        Block block = blockTree.FindBlock(blockParameter) ?? throw new InvalidOperationException($"Cannot find block {blockParameter}");
        tx.Hash ??= tx.CalculateHash();
        block = block.WithReplacedBodyCloned(BlockBody.WithOneTransactionOnly(tx));
        TransactionProcessorAdapterFactory previousAdapterFactory = transactionProcessorAdapter.CurrentAdapterFactory;
        transactionProcessorAdapter.CurrentAdapterFactory = static processor => new TraceTransactionProcessorAdapter(processor);

        try
        {
            return TraceImpl(block, tx.Hash, cancellationToken, options, useBlockAsBase: true, writer, pipeWriter);
        }
        finally
        {
            transactionProcessorAdapter.CurrentAdapterFactory = previousAdapterFactory;
        }
    }

    public GethLikeTxTrace? Trace(Hash256 txHash, GethTraceOptions traceOptions, CancellationToken cancellationToken, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null)
    {
        Hash256? blockHash = receiptStorage.FindBlockHash(txHash);
        if (blockHash is null) return null;

        Block? block = blockTree.FindBlock(blockHash, BlockTreeLookupOptions.RequireCanonical);
        if (block is null) return null;

        return TraceImpl(block, txHash, cancellationToken, traceOptions, writer: writer, pipeWriter: pipeWriter);
    }

    [Obsolete("Use the Hash256 overload: a block number resolves only the canonical block at that height.")]
    public GethLikeTxTrace? Trace(ulong blockNumber, int txIndex, GethTraceOptions options, CancellationToken cancellationToken, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null)
    {
        Block block = blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.RequireCanonical) ?? throw new InvalidOperationException($"No historical block found for {blockNumber}");
        if ((uint)txIndex >= (uint)block.Transactions.Length) throw new InvalidOperationException($"Block {blockNumber} has only {block.Transactions.Length} transactions and the requested tx index was {txIndex}");

        return TraceImpl(block, block.Transactions[txIndex].Hash, cancellationToken, options, writer: writer, pipeWriter: pipeWriter);
    }

    public GethLikeTxTrace? Trace(ulong blockNumber, Transaction tx, GethTraceOptions options, CancellationToken cancellationToken)
    {
        Block block = blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.RequireCanonical) ?? throw new InvalidOperationException($"No historical block found for {blockNumber}");
        if (tx.Hash is null) throw new InvalidOperationException("Cannot trace transactions without tx hash set.");

        block = block.WithReplacedBodyCloned(BlockBody.WithOneTransactionOnly(tx));
        using Scope<BlockProcessingComponents> scope = blockProcessingEnv.BuildAndOverride(block.Header, options.StateOverrides);
        IBlockTracer<GethLikeTxTrace> blockTracer = CreateOptionsTracer(block.Header, options with { TxHash = tx.Hash }, scope.Component.WorldState, specProvider);
        try
        {
            scope.Component.BlockchainProcessor.Process(block, TraceProcessingOptions.ReadOnlyReplay, blockTracer.WithCancellation(cancellationToken), cancellationToken);
            return blockTracer.BuildResult().SingleOrDefault();
        }
        catch
        {
            blockTracer.TryDispose();
            throw;
        }
    }

    public IReadOnlyCollection<GethLikeTxTrace> TraceBlock(BlockParameter blockParameter, GethTraceOptions options, CancellationToken cancellationToken, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null)
    {
        Block? block = blockTree.FindBlock(blockParameter);
        return TraceBlockImpl(block, options, cancellationToken, writer, pipeWriter);
    }

    public IReadOnlyCollection<GethLikeTxTrace> TraceBlock(Rlp blockRlp, GethTraceOptions options, CancellationToken cancellationToken, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null) =>
        TraceBlockImpl(GetBlockToTrace(blockRlp), options, cancellationToken, writer, pipeWriter, allowIndexed: false);

    public IReadOnlyCollection<GethLikeTxTrace> TraceBlock(Block block, GethTraceOptions options, CancellationToken cancellationToken, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null) =>
        TraceBlockImpl(block, options, cancellationToken, writer, pipeWriter, allowIndexed: false);

    public IReadOnlyCollection<Hash256> TraceBlockIntermediateRoots(Hash256 blockHash, GethTraceOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blockHash);
        ArgumentNullException.ThrowIfNull(options);

        // Mirror geth: canonical blocks first, fall back to the bad-block store so the diagnostic
        // use case (replaying a rejected block to find divergence) works.
        Block block = blockTree.FindBlock(blockHash)
                      ?? badBlockStore.GetAll().FirstOrDefault(b => b.Hash == blockHash)
                      ?? throw new InvalidOperationException($"Cannot find block {blockHash}");
        if (block.IsGenesis) throw new GenesisNotTraceableException();

        using Scope<BlockProcessingComponents> scope = blockProcessingEnv.BuildAndOverrideAtTarget(block.Header, options.StateOverrides);
        IntermediateRootsBlockTracer tracer = new(scope.Component.WorldState, specProvider.GetSpec(block.Header));
        scope.Component.BlockchainProcessor.Process(block, TraceProcessingOptions.ReadOnlyReplay, tracer.WithCancellation(cancellationToken), cancellationToken);
        return tracer.BuildResult();
    }

    public IEnumerable<string> TraceBlockToFile(Hash256 blockHash, GethTraceOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blockHash);
        ArgumentNullException.ThrowIfNull(options);

        Block block = blockTree.FindBlock(blockHash) ?? throw new InvalidOperationException($"No historical block found for {blockHash}");

        using Scope<BlockProcessingComponents> scope = blockProcessingEnv.BuildAndOverrideAtTarget(block.Header, options.StateOverrides);
        IReleaseSpec spec = specProvider.GetSpec(block.Header);
        GethLikeBlockFileTracer tracer = new(block, options, fileSystem, spec);
        scope.Component.BlockchainProcessor.Process(block, TraceProcessingOptions.ReadOnlyReplay, tracer.WithCancellation(cancellationToken), cancellationToken);

        return tracer.FileNames;
    }

    public IEnumerable<string> TraceBadBlockToFile(Hash256 blockHash, GethTraceOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blockHash);
        ArgumentNullException.ThrowIfNull(options);

        Block block = badBlockStore
                        .GetAll()
                        .FirstOrDefault(b => b.Hash == blockHash)
                    ?? throw new InvalidOperationException($"No historical block found for {blockHash}");
        using Scope<BlockProcessingComponents> scope = blockProcessingEnv.BuildAndOverrideAtTarget(block.Header, options.StateOverrides);
        IReleaseSpec spec = specProvider.GetSpec(block.Header);
        GethLikeBlockFileTracer tracer = new(block, options, fileSystem, spec);
        scope.Component.BlockchainProcessor.Process(block, TraceProcessingOptions.ReadOnlyReplay, tracer.WithCancellation(cancellationToken), cancellationToken);

        return tracer.FileNames;
    }

    private GethLikeTxTrace? TraceImpl(Block block, Hash256? txHash, CancellationToken cancellationToken, GethTraceOptions options,
        bool useBlockAsBase = false, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null, bool allowIndexed = true)
    {
        ArgumentNullException.ThrowIfNull(txHash);

        // For a synthetic tx trace (`debug_traceCall`), the base block must be the block itself rather than
        // its parent, so that state overrides applied in `BuildAndOverride` bind to the correct state root.
        if (options.BlockOverrides is not null || options.NoBaseFee)
        {
            block = block.WithReplacedBodyCloned(block.Body);
        }

        // The scope is opened before the block override lands on the header: the parent lookup keys on the
        // header's number, and an overridden number (zero included) would resolve the wrong state, or none.
        using Scope<BlockProcessingComponents> scope = useBlockAsBase
            ? blockProcessingEnv.BuildAndOverride(block.Header, options.StateOverrides, blockOverride: options.BlockOverrides)
            : blockProcessingEnv.BuildAndOverrideAtTarget(block.Header, options.StateOverrides);

        if (!useBlockAsBase)
        {
            options.BlockOverrides?.ApplyOverrides(block.Header);
        }
        if (options.NoBaseFee)
        {
            block.Header.BaseFeePerGas = UInt256.Zero;
        }

        GethTraceOptions filtered = options with { TxHash = txHash };
        long destroyRefund = (long)specProvider.GetSpec(block.Header).GasCosts.DestroyRefund;
        IBlockTracer<GethLikeTxTrace> tracer = writer is null
            ? CreateOptionsTracer(block.Header, filtered, scope.Component.WorldState, specProvider)
            : new GethLikeBlockStreamingMemoryTracer(filtered, writer, pipeWriter, cancellationToken, destroyRefund);

        try
        {
            bool unaltered = allowIndexed && options.StateOverrides is null && options.BlockOverrides is null && !options.NoBaseFee;
            IBlockTracer executionTracer = TransactionTraceBoundary.Wrap(
                tracer.WithCancellation(cancellationToken), useBlockAsBase ? null : txHash, unaltered ? prefixSeeds : null);
            scope.Component.BlockchainProcessor.Process(block, TraceProcessingOptions.ReadOnlyReplay, executionTracer, cancellationToken);
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
            { Tracer: var t } when GethLikeNativeTracerFactory.IsNativeTracer(t) => new GethLikeBlockNativeTracer(options.TxHash, (b, tx) => GethLikeNativeTracerFactory.CreateTracer(options, b, tx, worldState, specProvider.GetSpec(b.Header))),
            { Tracer.Length: > 0 } => CreateJavaScriptTracer(block, options, worldState, specProvider),
            _ => new GethLikeBlockMemoryTracer(options, (long)specProvider.GetSpec(block).GasCosts.DestroyRefund),
        };

    private static GethLikeBlockJavaScriptTracer CreateJavaScriptTracer(BlockHeader block, GethTraceOptions options, IWorldState worldState, ISpecProvider specProvider)
    {
        Engine.ValidateTracer(options.Tracer);
        return new GethLikeBlockJavaScriptTracer(worldState, specProvider.GetSpec(block), options);
    }

    private IReadOnlyCollection<GethLikeTxTrace> TraceBlockImpl(Block? block, GethTraceOptions options, CancellationToken cancellationToken, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null, bool allowIndexed = true)
    {
        ArgumentNullException.ThrowIfNull(block);

        // A block trace filtered to one transaction is that transaction's trace: the sequential tracer replays the
        // block and keeps one result, and the seeded single-transaction path produces the same result without the
        // replay. The block-level options the sequential path ignores stay with it.
        if (writer is null && options.TxHash is { } target && options.BlockOverrides is null && !options.NoBaseFee)
        {
            GethLikeTxTrace? single = TraceImpl(block, target, cancellationToken, options, allowIndexed: allowIndexed);
            IReadOnlyCollection<GethLikeTxTrace> filtered = single is null ? [] : [single];
            return new GethLikeTxTraceCollection(filtered);
        }

        if (allowIndexed && writer is null && options.TxHash is null && options.StateOverrides is null && parallelTracer is not null && !IsJavaScriptTracer(options)
            && parallelTracer.TryTrace(block, FindParent(block),
                (state, txHash) => CreateOptionsTracer(block.Header, options with { TxHash = txHash }, state, specProvider),
                afterTransactions: null, cancellationToken, out IReadOnlyList<GethLikeTxTrace>? parallel))
        {
            return new GethLikeTxTraceCollection(parallel);
        }

        using Scope<BlockProcessingComponents> scope = blockProcessingEnv.BuildAndOverrideAtTarget(block.Header, options.StateOverrides);

        long destroyRefund = (long)specProvider.GetSpec(block.Header).GasCosts.DestroyRefund;
        IBlockTracer<GethLikeTxTrace> tracer = writer is null
            ? CreateOptionsTracer(block.Header, options, scope.Component.WorldState, specProvider)
            : new GethLikeBlockEnvelopeStreamingTracer(options, writer, pipeWriter, cancellationToken, destroyRefund);

        try
        {
            scope.Component.BlockchainProcessor.Process(block, TraceProcessingOptions.ReadOnlyReplay, tracer.WithCancellation(cancellationToken), cancellationToken);
            // On the streaming path traces are written straight to the writer; the returned collection
            // is discarded by the caller, so avoid wrapping an empty BuildResult in a fresh collection.
            return writer is null
                ? new GethLikeTxTraceCollection(tracer.BuildResult())
                : Array.Empty<GethLikeTxTrace>();
        }
        catch
        {
            tracer.TryDispose();
            throw;
        }
    }

    /// <summary>A JavaScript tracer owns a script engine; one per worker at once is not a cost a block trace should pay.</summary>
    private static bool IsJavaScriptTracer(GethTraceOptions options) =>
        options.Tracer is { Length: > 0 } tracer && !GethLikeNativeTracerFactory.IsNativeTracer(tracer);

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
        Block block = Rlp.Decode<Block>(blockRlp)
            ?? throw new RlpException("Block decoded as null.");
        if (block.TotalDifficulty is null)
        {
            block.Header.TotalDifficulty = 1;
        }

        return block;
    }

    public record BlockProcessingComponents(IWorldState WorldState, BlockchainProcessorFacade BlockchainProcessor);
}
