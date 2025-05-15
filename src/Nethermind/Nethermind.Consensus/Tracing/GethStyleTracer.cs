// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
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

    public IEnumerable<string> TraceBlockToFile(Hash256 blockHash, GethTraceOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blockHash);
        ArgumentNullException.ThrowIfNull(options);

        var block = ResolveBlock(blockHash);

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

        var block = ResolveBadBlock(blockHash);
        var tracer = new GethLikeBlockFileTracer(block, options, _fileSystem);

        _processor.Process(block, ProcessingOptions.Trace, tracer.WithCancellation(cancellationToken), cancellationToken);

        return tracer.FileNames;
    }

    public IReadOnlyCollection<GethLikeTxTrace> TraceBlock(GethStyleTracerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Block);

        using IOverridableTxProcessingScope scope = _env.BuildAndOverride(request.Block.Header, request.Options.StateOverrides);
        IBlockTracer<GethLikeTxTrace> tracer = CreateOptionsTracer(request.Block.Header, request.Options, worldState, specProvider);

        ITransactionProcessorAdapter? currentAdapter = null;
        if (request.ProcessingOptions.HasFlag(ProcessingOptions.TraceTransactions))
        {
            currentAdapter = transactionProcessorAdapter.CurrentAdapter;
            transactionProcessorAdapter.CurrentAdapter = new TraceTransactionProcessorAdapter(transactionProcessorAdapter.TransactionProcessor);
        }

        try
        {
            _processor.Process(request.Block, request.ProcessingOptions, tracer.WithCancellation(cancellationToken), cancellationToken);
            return tracer.BuildResult();
        }
        catch
        {
            tracer.TryDispose();
            throw;
        }
        finally
        {
            if (currentAdapter is not null)
            {
                transactionProcessorAdapter.CurrentAdapter = currentAdapter;
            }
        }
    }

    public GethLikeTxTrace Trace(GethStyleTracerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.TxHash);

        return TraceBlock(request, cancellationToken).SingleOrDefault();
    }

    public GethStyleTracerRequest? ResolveTraceRequest(Hash256 txHash, GethTraceOptions options)
    {
        var (block, resolvedTxHash) = ResolveBlockAndTxHash(txHash);
        if (block is null)
        {
            return null;
        }

        return new GethStyleTracerRequest(block, resolvedTxHash, options);
    }

    public GethStyleTracerRequest? ResolveTraceRequest(long blockNumber, Transaction transaction, GethTraceOptions options)
    {
        var (block, txHash) = ResolveBlockAndTxHash(blockNumber, transaction);
        return new GethStyleTracerRequest(block, txHash, options);
    }

    public GethStyleTracerRequest? ResolveTraceRequest(long blockNumber, int txIndex, GethTraceOptions options)
    {
        var (block, txHash) = ResolveBlockAndTxHash(blockNumber, txIndex);
        return new GethStyleTracerRequest(block, txHash, options);
    }

    public GethStyleTracerRequest? ResolveTraceRequest(Hash256 blockHash, int txIndex, GethTraceOptions options)
    {
        var (block, txHash) = ResolveBlockAndTxHash(blockHash, txIndex);
        return new GethStyleTracerRequest(block, txHash, options);
    }

    public GethStyleTracerRequest? ResolveTraceRequest(Rlp blockRlp, Hash256 txHash, GethTraceOptions options)
    {
        Block block = ResolveBlock(blockRlp);
        return new GethStyleTracerRequest(block, txHash, options with { TxHash = txHash });
    }

    public GethStyleTracerRequest? ResolveTraceRequest(Block block, Hash256 txHash, GethTraceOptions options)
    {
        return new GethStyleTracerRequest(block, txHash, options with { TxHash = txHash });
    }

    public GethStyleTracerRequest? ResolveTraceRequest(BlockParameter blockParameter, Transaction tx, GethTraceOptions options)
    {
        var (block, txHash) = ResolveBlockAndTxHash(blockParameter, tx);
        return new GethStyleTracerRequest(block, txHash, options, ProcessingOptions.TraceTransactions);
    }

    public GethStyleTracerRequest? ResolveTraceBlockRequest(BlockParameter blockParameter, GethTraceOptions options)
    {
        Block? block = ResolveBlock(blockParameter);
        if (block is null)
        {
            return null;
        }

        if (!block.IsGenesis)
        {
            BlockHeader? parent = _blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.None);
            if (parent?.Hash is null)
            {
                throw new InvalidOperationException("Cannot trace blocks with invalid parents");
            }

            if (!_blockTree.IsMainChain(parent.Hash)) throw new InvalidOperationException("Cannot trace orphaned blocks");
        }

        return new GethStyleTracerRequest(block, null, options);
    }

    public GethStyleTracerRequest? ResolveTraceBlockRequest(Rlp blockRlp, GethTraceOptions options)
    {
        return ResolveTraceBlockRequest(ResolveBlock(blockRlp), options);
    }

    public GethStyleTracerRequest? ResolveTraceBlockRequest(Block block, GethTraceOptions options)
    {
        if (!block.IsGenesis)
        {
            BlockHeader? parent = _blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.None);
            if (parent?.Hash is null)
            {
                throw new InvalidOperationException("Cannot trace blocks with invalid parents");
            }

            if (!_blockTree.IsMainChain(parent.Hash)) throw new InvalidOperationException("Cannot trace orphaned blocks");
        }

        return new GethStyleTracerRequest(block, null, options);
    }

    public static IBlockTracer<GethLikeTxTrace> CreateOptionsTracer(BlockHeader block, GethTraceOptions options, IWorldState worldState, ISpecProvider specProvider) =>
        options switch
        {
            { Tracer: var t } when GethLikeNativeTracerFactory.IsNativeTracer(t) => new GethLikeBlockNativeTracer(options.TxHash, (b, tx) => GethLikeNativeTracerFactory.CreateTracer(options, b, tx, worldState)),
            { Tracer.Length: > 0 } => new GethLikeBlockJavaScriptTracer(worldState, specProvider.GetSpec(block), options),
            _ => new GethLikeBlockMemoryTracer(options),
        };

    private (Block? block, Hash256 txHash) ResolveBlockAndTxHash(Hash256 txHash)
    {
        Hash256? blockHash = _receiptStorage.FindBlockHash(txHash);
        if (blockHash is null)
        {
            return (null, default);
        }

        return (_blockTree.FindBlock(blockHash, BlockTreeLookupOptions.RequireCanonical), txHash);
    }

    private (Block block, Hash256 txHash) ResolveBlockAndTxHash(Hash256 blockHash, int txIndex)
    {
        Block block = _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.None)
                     ?? throw new InvalidOperationException($"No historical block found for {blockHash}");

        if (txIndex > block.Transactions.Length - 1)
            throw new InvalidOperationException($"Block {blockHash} has only {block.Transactions.Length} transactions and the requested tx index was {txIndex}");

        return (block, block.Transactions[txIndex].Hash);
    }

    private (Block block, Hash256 txHash) ResolveBlockAndTxHash(long blockNumber, int txIndex)
    {
        Block block = _blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.RequireCanonical)
                     ?? throw new InvalidOperationException($"No historical block found for {blockNumber}");

        if (txIndex > block.Transactions.Length - 1)
            throw new InvalidOperationException($"Block {blockNumber} has only {block.Transactions.Length} transactions and the requested tx index was {txIndex}");

        return (block, block.Transactions[txIndex].Hash);
    }

    private (Block block, Hash256 txHash) ResolveBlockAndTxHash(BlockParameter blockParameter, Transaction tx)
    {
        Block block = _blockTree.FindBlock(blockParameter)
                     ?? throw new InvalidOperationException($"Cannot find block {blockParameter}");

        tx.Hash ??= tx.CalculateHash();
        block = block.WithReplacedBodyCloned(BlockBody.WithOneTransactionOnly(tx));

        return (block, tx.Hash);
    }

    private (Block block, Hash256 txHash) ResolveBlockAndTxHash(long blockNumber, Transaction tx)
    {
        Block block = _blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.RequireCanonical)
                     ?? throw new InvalidOperationException($"No historical block found for {blockNumber}");

        if (tx.Hash is null)
            throw new InvalidOperationException("Cannot trace transactions without tx hash set.");

        block = block.WithReplacedBodyCloned(BlockBody.WithOneTransactionOnly(tx));

        return (block, tx.Hash);
    }

    private Block ResolveBlock(Hash256 blockHash)
    {
        return _blockTree.FindBlock(blockHash)
               ?? throw new InvalidOperationException($"No historical block found for {blockHash}");
    }

    private Block? ResolveBlock(BlockParameter parameter)
    {
        return _blockTree.FindBlock(parameter);
    }

    private static Block ResolveBlock(Rlp blockRlp)
    {
        Block block = Rlp.Decode<Block>(blockRlp);
        if (block.TotalDifficulty is null)
        {
            block.Header.TotalDifficulty = 1;
        }
        return block;
    }

    private Block ResolveBadBlock(Hash256 blockHash)
    {
        return _badBlockStore
                .GetAll()
                .FirstOrDefault(b => b.Hash == blockHash)
            ?? throw new InvalidOperationException($"No historical block found for {blockHash}");
    }
}
