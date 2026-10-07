// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Autofac.Features.AttributeFilters;
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
using Nethermind.Evm;
using Nethermind.State.OverridableEnv;
using Nethermind.Evm.Tracing;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Tracing;

#pragma warning disable NETH003 // Build variant: excluded from the zkEVM build, which does no tracing
public class GethStyleTracer(
    [KeyFilter(IReceiptFinder.RegenerableKey)] IReceiptFinder receiptFinder,
    IBlockTree blockTree,
    IBadBlockStore badBlockStore,
    ISpecProvider specProvider,
    ChangeableTransactionProcessorAdapter transactionProcessorAdapter,
    IFileSystem fileSystem,
    IOverridableEnv<GethStyleTracer.BlockProcessingComponents> blockProcessingEnv,
    IPrefixStateSeedSource prefixSeeds,
    IOverridableCodeInfoRepository codeInfoRepository,
    IParallelBlockTracer? parallelTracer = null,
    GethStyleTracer.TraceCallRequestState? callRequestState = null
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
        if (options.TxIndex is { } index)
            return TraceCallAtIndex(block, tx, index, options, cancellationToken, writer, pipeWriter);

        block = block.WithReplacedBodyCloned(BlockBody.WithOneTransactionOnly(tx));
        TransactionProcessorAdapterFactory previousAdapterFactory = transactionProcessorAdapter.CurrentAdapterFactory;
        (BlockHeader callHeader, IReleaseSpec callSpec) = PrepareCallHeader(block, options);
        UInt256? blobBaseFee = GetCallBlobBaseFee(tx, options);
        transactionProcessorAdapter.CurrentAdapterFactory = processor =>
            blobBaseFee is not null || options.BlockOverrides?.PrevRandao is not null
                ? CreateCallAdapter(processor, callHeader, callSpec, options.BlockOverrides, blobBaseFee)
                : new TraceCallTransactionProcessorAdapter(processor, options.BlockOverrides, blobBaseFee);

        try
        {
            if (callRequestState is not null)
            {
                callRequestState.BlobBaseFee = blobBaseFee;
                callRequestState.BlockhashLookup = CreateBlockhashLookup(block.Header, options);
            }
            return TraceImpl(block, tx.Hash, cancellationToken, options, useBlockAsBase: true, writer, pipeWriter);
        }
        finally
        {
            transactionProcessorAdapter.CurrentAdapterFactory = previousAdapterFactory;
            if (callRequestState is not null)
            {
                callRequestState.BlobBaseFee = null;
                callRequestState.BlockhashLookup = null;
            }
        }
    }

    private GethLikeTxTrace? TraceCallAtIndex(Block block, Transaction call, ulong index, GethTraceOptions options,
        CancellationToken cancellationToken, Utf8JsonWriter? writer, PipeWriter? pipeWriter)
    {
        Block replay = CreateCallReplay(block, call, index);
        (BlockHeader callHeader, IReleaseSpec callSpec) = PrepareCallHeader(block, options);
        using Scope<BlockProcessingComponents> scope = blockProcessingEnv.BuildAndOverride(FindParent(block));
        IWorldState state = scope.Component.WorldState;
        bool tracePreceding = WantsLogIndex(options);
        using GethLikeBlockCallDeadlineTracer tracer = new(options with { TxHash = call.Hash }, cancellationToken,
            timedOptions => CreateIndexedCallTracer(callHeader, call, timedOptions, state, callSpec, cancellationToken, writer, pipeWriter, tracePreceding));
        TransactionProcessorAdapterFactory previous = transactionProcessorAdapter.CurrentAdapterFactory;
        try
        {
            // Prefix execution uses canonical state and block context. Overrides belong only to the synthetic call.
            CallAtIndexBlockTracer callTracer = new(tracer.WithCancellation(tracer.Token), callHeader, call,
                tracedBlock => PrepareIndexedCall(tracedBlock, call, options, state, callSpec, tracer.Token));
            IBlockTracer boundary = TransactionTraceBoundary.Wrap(callTracer, call.Hash);
            scope.Component.BlockchainProcessor.Process(replay, TraceProcessingOptions.ReadOnlyReplay, boundary, tracer.Token);
            if (!callTracer.IsPrepared) throw new InvalidOperationException($"The synthetic call at index {index} in block {block.Hash} was not prepared for tracing.");
            return (tracePreceding ? KeepTrace(tracer.BuildResult(), call.Hash) : tracer.BuildResult()).SingleOrDefault();
        }
        catch (Exception ex) when (tracer.Expired)
        {
            return tracer.CompleteExpired(ex).SingleOrDefault();
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            transactionProcessorAdapter.CurrentAdapterFactory = previous;
            if (callRequestState is not null)
            {
                callRequestState.BlobBaseFee = null;
                callRequestState.BlockhashLookup = null;
            }
        }
    }

    private static TraceCallBlockhashProvider.Lookup? CreateBlockhashLookup(BlockHeader header, GethTraceOptions options)
    {
        if (options.BlockOverrides?.Number is not { } number || number == header.Number) return null;

        BlockHeader reference = header.Clone();
        // Geth retains the selected header for GetHashFn, except when simulating its immediate successor.
        if (header.Number != ulong.MaxValue && number == header.Number + 1)
        {
            reference.ParentHash = header.Hash;
            reference.Number = number;
        }
        return new TraceCallBlockhashProvider.Lookup(reference, number);
    }

    private (BlockHeader Header, IReleaseSpec Spec) PrepareCallHeader(Block block, GethTraceOptions options)
    {
        BlockHeader header = block.Header.Clone();
        options.BlockOverrides?.ApplyOverrides(header);
        if (options.NoBaseFee) header.BaseFeePerGas = UInt256.Zero;
        return (header, specProvider.GetSpec(header));
    }

    private IBlockTracer<GethLikeTxTrace> CreateIndexedCallTracer(BlockHeader header, Transaction call,
        GethTraceOptions options, IWorldState state, IReleaseSpec spec, CancellationToken cancellationToken,
        Utf8JsonWriter? writer, PipeWriter? pipeWriter, bool tracePreceding)
    {
        GethTraceOptions filtered = options with { TxHash = tracePreceding ? null : call.Hash };
        BlockLogIndex logIndex = new();
        return writer is null
            ? CreateOptionsTracer(header, filtered, state, specProvider, tracePreceding ? (_, _) => logIndex : null, isTraceCall: true)
            : new GethLikeBlockStreamingMemoryTracer(filtered, writer, pipeWriter, cancellationToken, (long)spec.GasCosts.DestroyRefund);
    }

    private static Block CreateCallReplay(Block block, Transaction call, ulong index)
    {
        if (block.IsGenesis) throw new GenesisNotTraceableException();
        if (index >= (ulong)Math.Max(block.Transactions.Length, 1))
            throw new ArgumentOutOfRangeException(nameof(index));

        Transaction[] transactions = new Transaction[(int)index + 1];
        block.Transactions.AsSpan(0, (int)index).CopyTo(transactions);
        transactions[^1] = call;
        return block.WithReplacedBodyCloned(block.Body.WithChangedTransactions(transactions));
    }

    private void PrepareIndexedCall(Block tracedBlock, Transaction call, GethTraceOptions options, IWorldState state, IReleaseSpec callSpec, CancellationToken cancellationToken)
    {
        UInt256? blobBaseFee = GetCallBlobBaseFee(call, options);
        if (callRequestState is not null)
        {
            callRequestState.BlobBaseFee = blobBaseFee;
            callRequestState.BlockhashLookup = CreateBlockhashLookup(tracedBlock.Header, options);
            if (callRequestState.BlockhashLookup is { } lookup) lookup.Token = cancellationToken;
        }
        options.BlockOverrides?.ApplyOverrides(tracedBlock.Header);
        if (options.NoBaseFee) tracedBlock.Header.BaseFeePerGas = UInt256.Zero;
        IReleaseSpec overrideSpec = callSpec.WithoutEip158();
        state.ApplyStateOverridesNoCommit(codeInfoRepository, options.StateOverrides, overrideSpec);
        state.Commit(overrideSpec);
        transactionProcessorAdapter.CurrentAdapterFactory = processor =>
        {
            // This Ethereum context does not invoke chain-specific BlockProcessor context overrides (for example XDC).
            return CreateCallAdapter(processor, tracedBlock.Header, callSpec, options.BlockOverrides, blobBaseFee);
        };
    }

    private static UInt256? GetCallBlobBaseFee(Transaction call, GethTraceOptions options) =>
        call.MaxFeePerBlobGas is { IsZero: true } ? UInt256.Zero : options.BlockOverrides?.BlobBaseFee;

    private static TraceCallTransactionProcessorAdapter CreateCallAdapter(ITransactionProcessor processor,
        BlockHeader header, IReleaseSpec spec, BlockOverride? overrides, UInt256? blobBaseFee)
    {
        TraceCallTransactionProcessorAdapter adapter = new(processor, overrides, blobBaseFee);
        adapter.SetBlockExecutionContext(new BlockExecutionContext(header, spec));
        return adapter;
    }

    private sealed class TraceCallTransactionProcessorAdapter(ITransactionProcessor processor, BlockOverride? overrides, UInt256? blobBaseFee) : ITransactionProcessorAdapter
    {
        public TransactionResult Execute(Transaction transaction, ITxTracer tracer) => processor.Trace(transaction, tracer);

        public void SetBlockExecutionContext(in BlockExecutionContext context)
        {
            if (blobBaseFee is { } fee)
            {
                processor.SetBlockExecutionContext(BlockExecutionContext.WithPrevRandaoAndBlobBaseFee(
                    context.Header, context.Spec, overrides?.PrevRandao?.ValueHash256 ?? context.PrevRandao, fee));
            }
            else if (overrides?.PrevRandao is { } prevRandao)
            {
                processor.SetBlockExecutionContext(BlockExecutionContext.WithPrevRandao(context.Header, context.Spec, prevRandao.ValueHash256));
            }
            else
            {
                processor.SetBlockExecutionContext(context);
            }
        }
    }

    private sealed class CallAtIndexBlockTracer(IBlockTracer inner, BlockHeader callHeader, Transaction call, Action<Block> prepareCall) : IBlockTracer
    {
        private Block _block = null!;
        public bool IsPrepared { get; private set; }
        public bool IsTracingRewards => inner.IsTracingRewards;
        public void StartNewBlockTrace(Block block)
        {
            _block = block;
            // Tracers capture the call context now; prefix execution still uses its separate canonical header.
            inner.StartNewBlockTrace(block.WithReplacedHeader(callHeader));
        }
        public ITxTracer StartNewTxTrace(Transaction? transaction)
        {
            if (ReferenceEquals(transaction, call))
            {
                prepareCall(_block);
                IsPrepared = true;
            }
            return inner.StartNewTxTrace(transaction);
        }
        public void EndTxTrace() => inner.EndTxTrace();
        public void EndBlockTrace() => inner.EndBlockTrace();
        public void ReportReward(Address author, string rewardType, UInt256 rewardValue) => inner.ReportReward(author, rewardType, rewardValue);
    }

    public GethLikeTxTrace? Trace(Hash256 txHash, GethTraceOptions traceOptions, CancellationToken cancellationToken, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null)
    {
        Hash256? blockHash = receiptFinder.FindBlockHash(txHash);
        if (blockHash is null) return null;

        Block? block = blockTree.FindBlock(blockHash, BlockTreeLookupOptions.RequireCanonical);
        if (block is null) return null;

        return TraceImpl(block, txHash, cancellationToken, traceOptions, writer: writer, pipeWriter: pipeWriter, isTraceTransaction: true);
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
        finally
        {
            blockTracer.TryDispose();
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
        bool useBlockAsBase = false, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null, bool allowIndexed = true, bool isTraceTransaction = false)
    {
        ArgumentNullException.ThrowIfNull(txHash);

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

        bool unaltered = allowIndexed && options.StateOverrides is null && options.BlockOverrides is null && !options.NoBaseFee;
        BlockLogIndex? logIndex = null;
        bool tracePreceding = false;
        if (!useBlockAsBase && WantsLogIndex(options))
        {
            // Stored receipts describe only the unaltered canonical body; anything else numbers its logs from the
            // transactions it runs, which then have to be traced too.
            int txIndex = Array.FindIndex(block.Transactions, t => t.Hash == txHash);
            int[]? firstLogIndexes = unaltered && txIndex >= 0 ? FirstLogIndexes(block) : null;
            tracePreceding = firstLogIndexes is null;
            logIndex = new BlockLogIndex(tracePreceding ? 0 : firstLogIndexes![txIndex]);
        }

        GethTraceOptions filtered = options with { TxHash = tracePreceding ? null : txHash };
        long destroyRefund = (long)specProvider.GetSpec(block.Header).GasCosts.DestroyRefund;
        IBlockTracer<GethLikeTxTrace> CreateTracer(GethTraceOptions traceOptions) => writer is null
            ? CreateOptionsTracer(block.Header, traceOptions, scope.Component.WorldState, specProvider, logIndex is null ? null : (_, _) => logIndex, isTraceCall: useBlockAsBase)
            : new GethLikeBlockStreamingMemoryTracer(traceOptions, writer, pipeWriter, cancellationToken, destroyRefund);
        using GethLikeBlockCallDeadlineTracer? deadline = useBlockAsBase || isTraceTransaction
            ? new(options with { TxHash = txHash }, cancellationToken,
                traceOptions => CreateTracer(traceOptions with { TxHash = filtered.TxHash }))
            : null;
        IBlockTracer<GethLikeTxTrace> tracer = deadline ?? CreateTracer(filtered);
        CancellationToken executionToken = deadline?.Token ?? cancellationToken;
        if (useBlockAsBase && callRequestState?.BlockhashLookup is { } lookup) lookup.Token = executionToken;

        try
        {
            // Prefix seeds do not contain the preceding receipts needed for block-wide log indices.
            IBlockTracer executionTracer = TransactionTraceBoundary.Wrap(
                tracer.WithCancellation(executionToken), useBlockAsBase ? null : txHash, unaltered && !tracePreceding && (!RequiresLogIndices(options) || logIndex is not null) ? prefixSeeds : null);
            scope.Component.BlockchainProcessor.Process(block, TraceProcessingOptions.ReadOnlyReplay, executionTracer, executionToken);
            return (tracePreceding ? KeepTrace(tracer.BuildResult(), txHash) : tracer.BuildResult()).SingleOrDefault();
        }
        catch (Exception ex) when (deadline?.Expired == true)
        {
            return deadline.CompleteExpired(ex).SingleOrDefault();
        }
        catch when (deadline is not null && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            if (deadline is null) tracer.TryDispose();
        }
    }

    public static IBlockTracer<GethLikeTxTrace> CreateOptionsTracer(BlockHeader block, GethTraceOptions options, IWorldState worldState, ISpecProvider specProvider, Func<Block, Transaction, BlockLogIndex>? logIndex = null) =>
        CreateOptionsTracer(block, options, worldState, specProvider, logIndex, isTraceCall: false);

    private static IBlockTracer<GethLikeTxTrace> CreateOptionsTracer(BlockHeader block, GethTraceOptions options, IWorldState worldState, ISpecProvider specProvider, Func<Block, Transaction, BlockLogIndex>? logIndex, bool isTraceCall) =>
        options switch
        {
            { Tracer: GethLikeBlockMuxTracer.TracerName } => new GethLikeBlockMuxTracer(options,
                child => CreateOptionsTracer(block, child, worldState, specProvider, null, isTraceCall)),
            { Tracer: GethLikeBlockFlatCallTracer.TracerName } => new GethLikeBlockFlatCallTracer(options, specProvider.GetSpec(block), isTraceCall),
            { Tracer: GethLikeBlockErc7562Tracer.TracerName } => new GethLikeBlockErc7562Tracer(options, worldState, specProvider),
            _ when logIndex is null && RequiresLogIndices(options) => new GethLikeBlockCallTracer(options.TxHash, (b, tx) => new NativeCallTracer(tx, specProvider.GetSpec(b.Header), options)),
            { Tracer: var t } when GethLikeNativeTracerFactory.IsNativeTracer(t) => new GethLikeBlockNativeTracer(options.TxHash, (b, tx) => GethLikeNativeTracerFactory.CreateTracer(logIndex is null ? options : options with { LogIndex = logIndex(b, tx) }, b, tx, worldState, specProvider.GetSpec(b.Header))),
            { Tracer.Length: > 0 } => new GethLikeBlockJavaScriptTracer(worldState, specProvider.GetSpec(block), options),
            _ => new GethLikeBlockMemoryTracer(options, (long)specProvider.GetSpec(block).GasCosts.DestroyRefund),
        };

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

        bool wantsLogIndex = WantsLogIndex(options);
        if (allowIndexed && writer is null && options.TxHash is null && options.StateOverrides is null && parallelTracer is not null && !IsJavaScriptTracer(options)
            && (!RequiresLogIndices(options) || wantsLogIndex)
            && TryGetParallelLogIndex(block, wantsLogIndex, out Func<Block, Transaction, BlockLogIndex>? parallelLogIndex)
            && parallelTracer.TryTrace(block, FindParent(block),
                (state, txHash) => CreateOptionsTracer(block.Header, options with { TxHash = txHash }, state, specProvider, parallelLogIndex),
                afterTransactions: null, cancellationToken, out IReadOnlyList<GethLikeTxTrace>? parallel))
        {
            return new GethLikeTxTraceCollection(parallel);
        }

        // The replay numbers logs from the body it runs, so a filtered trace still traces the transactions before
        // its target to count their logs.
        Hash256? keptTxHash = wantsLogIndex ? options.TxHash : null;
        if (keptTxHash is not null)
        {
            options = options with { TxHash = null };
        }

        BlockLogIndex sharedLogIndex = new();

        using Scope<BlockProcessingComponents> scope = blockProcessingEnv.BuildAndOverrideAtTarget(block.Header, options.StateOverrides);

        long destroyRefund = (long)specProvider.GetSpec(block.Header).GasCosts.DestroyRefund;
        IBlockTracer<GethLikeTxTrace> tracer = writer is null
            ? CreateOptionsTracer(block.Header, options, scope.Component.WorldState, specProvider, (_, _) => sharedLogIndex)
            : new GethLikeBlockEnvelopeStreamingTracer(options, writer, pipeWriter, cancellationToken, destroyRefund);

        try
        {
            IBlockTracer executionTracer = TransactionTraceBoundary.Wrap(tracer.WithCancellation(cancellationToken), keptTxHash);
            scope.Component.BlockchainProcessor.Process(block, TraceProcessingOptions.ReadOnlyReplay, executionTracer, cancellationToken);
            // On the streaming path traces are written straight to the writer; the returned collection
            // is discarded by the caller, so avoid wrapping an empty BuildResult in a fresh collection.
            if (writer is not null) return Array.Empty<GethLikeTxTrace>();

            return new GethLikeTxTraceCollection(keptTxHash is null ? tracer.BuildResult() : KeepTrace(tracer.BuildResult(), keptTxHash));
        }
        finally
        {
            tracer.TryDispose();
        }
    }

    private static bool RequiresLogIndices(GethTraceOptions options)
    {
        if (options.Tracer == GethLikeBlockMuxTracer.TracerName)
        {
            foreach ((string name, JsonElement childConfig) in GethLikeBlockMuxTracer.ParseConfig(options.TracerConfig))
                if (RequiresLogIndices(options with { Tracer = name, TracerConfig = childConfig })) return true;
            return false;
        }
        if (options.Tracer is not (NativeCallTracer.CallTracer or GethLikeBlockErc7562Tracer.TracerName) || options.TracerConfig is not { ValueKind: JsonValueKind.Object } config)
            return false;

        bool withLog = false;
        foreach (JsonProperty property in config.EnumerateObject())
        {
            if (property.Name.Equals("withLog", StringComparison.OrdinalIgnoreCase))
                withLog = property.Value.ValueKind == JsonValueKind.True;
        }
        return withLog;
    }

    private static bool WantsLogIndex(GethTraceOptions options) =>
        options.Tracer == NativeCallTracer.CallTracer
        && TypeInfoJsonSerializer.Deserialize<NativeCallTracerConfig>(options.TracerConfig, EthereumJsonSerializer.JsonOptions)?.WithLog == true;

    private static List<GethLikeTxTrace> KeepTrace(IReadOnlyCollection<GethLikeTxTrace> traces, Hash256 txHash)
    {
        List<GethLikeTxTrace> kept = [];
        foreach (GethLikeTxTrace trace in traces)
        {
            if (trace.TxHash == txHash) kept.Add(trace);
            else trace.Dispose();
        }

        return kept;
    }

    /// <summary>Seeds each parallel worker's log index from the block's receipts, resolved once on the calling thread.</summary>
    /// <returns><c>false</c> when the indexes are wanted but the receipts cannot supply them, leaving the block to the
    /// sequential replay, which numbers logs from the body.</returns>
    private bool TryGetParallelLogIndex(Block block, bool wantsLogIndex, out Func<Block, Transaction, BlockLogIndex>? logIndex)
    {
        logIndex = null;
        if (!wantsLogIndex) return true;

        int[]? firstLogIndexes = FirstLogIndexes(block);
        if (firstLogIndexes is null) return false;

        Dictionary<Hash256, int> firstLogIndexByTx = new(block.Transactions.Length);
        for (int i = 0; i < block.Transactions.Length; i++)
        {
            firstLogIndexByTx[block.Transactions[i].Hash!] = firstLogIndexes[i];
        }

        // A fresh counter per call: a worker may retry a transaction, and each tracer advances its own.
        logIndex = (_, tx) => new BlockLogIndex(firstLogIndexByTx[tx.Hash!]);
        return true;
    }

    /// <summary>The block-wide index of each transaction's first log, from the block's receipts.</summary>
    /// <returns><c>null</c> when the receipts are neither stored nor reproducible, or do not match the body.</returns>
    private int[]? FirstLogIndexes(Block block)
    {
        TxReceipt[] receipts;
        try
        {
            receipts = receiptFinder.Get(block);
        }
        catch (ResourceNotFoundException)
        {
            // The caller falls back to counting the logs of the body it replays.
            return null;
        }

        if (receipts.Length != block.Transactions.Length) return null;

        int[] firstLogIndexes = new int[receipts.Length];
        int next = 0;
        for (int i = 0; i < receipts.Length; i++)
        {
            firstLogIndexes[i] = next;
            next += receipts[i].Logs?.Length ?? 0;
        }

        return firstLogIndexes;
    }

    /// <summary>A JavaScript tracer owns a script engine; one per worker at once is not a cost a block trace should pay.</summary>
    private static bool IsJavaScriptTracer(GethTraceOptions options) =>
        options.Tracer is { Length: > 0 } tracer && tracer != GethLikeBlockErc7562Tracer.TracerName && !GethLikeNativeTracerFactory.IsNativeTracer(tracer);

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

    /// <summary>Holds overrides while executing a synthetic trace call.</summary>
    public sealed class TraceCallRequestState
    {
        /// <summary>The synthetic call blob fee, or null during canonical execution.</summary>
        public UInt256? BlobBaseFee { get; set; }

        internal TraceCallBlockhashProvider.Lookup? BlockhashLookup { get; set; }
    }

    public record BlockProcessingComponents(IWorldState WorldState, BlockchainProcessorFacade BlockchainProcessor);
}
