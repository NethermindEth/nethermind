// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// A one-off <see cref="IBlockchainProcessor"/> that runs <see cref="Process"/> on its own scope's
/// <see cref="IBranchProcessor"/> instead of delegating to the main chain processor, so it never
/// touches the main processing queue.
/// </summary>
/// <remarks>
/// The exclusive lock serializes calls because the wrapped scope's world state and branch processor
/// are single-use. The head is never updated: all consumers pass <see cref="ProcessingOptions.DoNotUpdateHead"/>,
/// so the processed branch is left for the caller to commit (e.g. by suggesting the sealed block back
/// into the main pipeline).
/// </remarks>
public sealed class OneTimeChainProcessor(
    IWorldState worldState,
    IBlockTree blockTree,
    IBranchProcessor branchProcessor,
    ISpecProvider specProvider,
    IReadOnlyList<IBlockPreprocessorStep> preprocessorSteps,
    IStateReader stateReader,
    ILogManager logManager,
    IEnumerable<IBlockTracer>? blockTracers = null
) : IBlockchainProcessor
{
    private const int MaxBranchSize = 8192;

    private readonly IWorldState _worldState = worldState;
    private readonly IBranchProcessor _branchProcessor = branchProcessor;
    private readonly ISpecProvider _specProvider = specProvider;
    private readonly IReadOnlyList<IBlockPreprocessorStep> _preprocessorSteps = preprocessorSteps;
    private readonly IStateReader _stateReader = stateReader;
    private readonly IBlockTree _blockTree = blockTree;
    private readonly ILogger _logger = logManager.GetClassLogger<OneTimeChainProcessor>();
    private readonly Lock _lock = new();
    // Retained for DI parity with BlockchainProcessor; seeding into a composite tracer is only meaningful
    // on the queued path, which this processor deliberately does not have.
    private readonly IEnumerable<IBlockTracer>? _blockTracers = blockTracers;

    public Block? Process(Block suggestedBlock, ProcessingOptions options, IBlockTracer tracer, CancellationToken token = default)
    {
        lock (_lock)
        {
            return ProcessCore(suggestedBlock, options, tracer, token);
        }
    }

    private Block? ProcessCore(Block suggestedBlock, ProcessingOptions options, IBlockTracer tracer, CancellationToken token)
    {
        if (!RunSimpleChecksAheadOfProcessing(suggestedBlock, options))
        {
            return null;
        }

        UInt256 totalDifficulty = suggestedBlock.TotalDifficulty ?? 0;
        if (_logger.IsTrace) _logger.Trace($"Total difficulty of block {suggestedBlock.ToString(Block.Format.Short)} is {totalDifficulty}");

        bool shouldProcess =
            suggestedBlock.IsGenesis
            || _blockTree.IsBetterThanHead(suggestedBlock.Header)
            || options.ContainsFlag(ProcessingOptions.ForceProcessing);

        if (!shouldProcess)
        {
            if (_logger.IsDebug) _logger.Debug($"Skipped processing of {suggestedBlock.ToString(Block.Format.FullHashAndNumber)}, Head = {_blockTree.Head?.Header?.ToString(BlockHeader.Format.Short)}, total diff = {totalDifficulty}, head total diff = {_blockTree.Head?.TotalDifficulty}");
            return null;
        }

        using ProcessingBranch processingBranch = PrepareProcessingBranch(suggestedBlock, options);
        PrepareBlocksToProcess(suggestedBlock, options, processingBranch, token);

        Block[]? processedBlocks = ProcessBranch(processingBranch, options, tracer, token);
        if (processedBlocks is null)
        {
            return null;
        }

        Block? lastProcessed = null;
        if (processedBlocks.Length > 0)
        {
            lastProcessed = processedBlocks[^1];
            if (_logger.IsTrace) _logger.Trace($"Setting total on last processed to {lastProcessed.ToString(Block.Format.Short)}");
            lastProcessed.Header.TotalDifficulty = suggestedBlock.TotalDifficulty;
        }
        else
        {
            if (_logger.IsDebug) _logger.Debug($"Skipped processing of {suggestedBlock.ToString(Block.Format.FullHashAndNumber)}, last processed is null: {true}, processedBlocks.Length: {processedBlocks.Length}");
        }

        return lastProcessed;
    }

    private Block[]? ProcessBranch(in ProcessingBranch processingBranch, ProcessingOptions options, IBlockTracer tracer, CancellationToken token)
    {
        Block[]? processedBlocks;
        try
        {
            processedBlocks = _branchProcessor.Process(
                processingBranch.BaseBlock,
                processingBranch.BlocksToProcess,
                options,
                tracer,
                token);
        }
        catch (InvalidBlockException ex)
        {
            if (_logger.IsWarn) _logger.Warn($"Issue processing block {ex.InvalidBlock} {ex}");

            // Previously invalid blocks were deleted from the block tree except when processed with
            // ReadOnlyChain, which is the case with _blocksConfig.BuildBlocksOnMainState; they are now
            // always kept.
            processedBlocks = null;
        }

        return processedBlocks;
    }

    private void PrepareBlocksToProcess(Block suggestedBlock, ProcessingOptions options, ProcessingBranch processingBranch, CancellationToken token)
    {
        ArrayPoolList<Block> blocksToProcess = processingBranch.BlocksToProcess;
        if (options.ContainsFlag(ProcessingOptions.ForceProcessing))
        {
            processingBranch.Blocks.Clear(); // TODO: investigate why if we clear it all we need to collect and iterate on all the blocks in PrepareProcessingBranch?
            blocksToProcess.Add(suggestedBlock);
        }
        else
        {
            foreach (Block block in processingBranch.Blocks.AsSpan())
            {
                token.ThrowIfCancellationRequested();

                if (block.Hash is not null && _blockTree.WasProcessed(block.Number, block.Hash))
                {
                    if (_logger.IsInfo) _logger.Info($"Rerunning block after reorg or pruning: {block.ToString(Block.Format.Short)}");
                }

                blocksToProcess.Add(block);
            }

            Block firstBlock = blocksToProcess[0];
            if (!firstBlock.IsGenesis)
            {
                BlockHeader? parentOfFirstBlock = _blockTree.FindHeader(firstBlock.ParentHash!, BlockTreeLookupOptions.None) ?? throw new InvalidBlockException(firstBlock, $"Rejected a block from a different fork: {firstBlock.ToString(Block.Format.FullHashAndNumber)}");
                if (!_stateReader.HasStateForBlock(parentOfFirstBlock))
                {
                    ThrowOrphanedBlock(firstBlock);
                }
            }
        }

        if (_logger.IsTrace) TraceProcessingBlocks(processingBranch, blocksToProcess);

        for (int i = 0; i < blocksToProcess.Count; i++)
        {
            /* this can happen if the block was loaded as an ancestor and did not go through the recovery queue */
            Preprocess(blocksToProcess[i]);
        }

        // Uncommon logging and throws

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceProcessingBlocks(ProcessingBranch processingBranch, ArrayPoolList<Block> blocksToProcess)
            => _logger.Trace($"Processing {blocksToProcess.Count} blocks from state root {processingBranch.BaseBlock}");

        [DoesNotReturn, StackTraceHidden]
        static void ThrowOrphanedBlock(Block firstBlock)
            => throw new InvalidBlockException(firstBlock, $"Rejected a block that is orphaned: {firstBlock.ToString(Block.Format.FullHashAndNumber)}");

    }

    private ProcessingBranch PrepareProcessingBranch(Block suggestedBlock, ProcessingOptions options)
    {
        BlockHeader? branchingPoint = null;
        ArrayPoolList<Block> blocksToBeAddedToMain = new((int)Reorganization.PersistenceInterval);

        bool branchingCondition;

        Block toBeProcessed = suggestedBlock;
        long iterations = 0;
        bool isTrace = _logger.IsTrace;
        do
        {
            iterations++;
            if (iterations > MaxBranchSize)
            {
                ThrowMaxBranchSizeReached();
            }

            if (!options.ContainsFlag(ProcessingOptions.Trace))
            {
                blocksToBeAddedToMain.Add(toBeProcessed);
            }

            if (isTrace) TraceProcessingBlock(suggestedBlock, toBeProcessed);
            if (toBeProcessed.IsGenesis)
            {
                break;
            }

            branchingPoint = _blockTree.FindParentHeader(toBeProcessed.Header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);

            if (branchingPoint is null)
            {
                // genesis block
                break;
            }

            if (options.ContainsFlag(ProcessingOptions.IgnoreParentNotOnMainChain))
            {
                break;
            }

            if (isTrace) TraceParentSearch(toBeProcessed);

            toBeProcessed = _blockTree.FindParent(toBeProcessed.Header, BlockTreeLookupOptions.None);

            if (isTrace) TraceParentBlock(toBeProcessed);

            if (toBeProcessed is null)
            {
                if (_logger.IsDebug) DebugParentNotFound(suggestedBlock);
                break;
            }

            // We only walk back far enough to find a base block that still has state: those are the blocks
            // that actually need (re)processing. Blocks deeper than that already have state and must not be
            // reprocessed - moving them onto the main chain (down to the real reorg boundary) is handled by
            // BlockTree.TryUpdateMainChain, which walks headers there cheaply. Hence MaxBranchSize now bounds
            // only the blocks-without-state we collect here, not the whole reorg depth.
            bool hasState = toBeProcessed.StateRoot is null || _stateReader.HasStateForBlock(toBeProcessed.Header);
            bool notInForceProcessing = !options.ContainsFlag(ProcessingOptions.ForceProcessing);
            branchingCondition = !hasState && notInForceProcessing;

            // notFoundTheBranchingPointYet no longer gates the loop; compute the IsMainChain lookup only for the trace.
            if (isTrace) TraceBranchingConditions(branchingPoint, !_blockTree.IsMainChain(branchingPoint.Hash!), hasState, notInForceProcessing);

        } while (branchingCondition);

        if (isTrace)
        {
            TraceBranchingPoint(branchingPoint);
        }

        Hash256 stateRoot = branchingPoint?.StateRoot;
        if (isTrace) TraceStateRootLookup(stateRoot);

        if (blocksToBeAddedToMain.Count > 1)
            blocksToBeAddedToMain.Reverse();

        return new ProcessingBranch(branchingPoint, blocksToBeAddedToMain);

        // Uncommon logging and throws

        [MethodImpl(MethodImplOptions.NoInlining)]
        void DebugParentNotFound(Block suggestedBlock)
            => _logger.Debug($"Treating this as fast sync transition for {suggestedBlock.ToString(Block.Format.Short)}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceBranchingConditions(BlockHeader branchingPoint, bool notFoundTheBranchingPointYet, bool hasState, bool notInForceProcessing) => _logger.Trace(
                $" Current branching point: " +
                $"{branchingPoint.Number}," +
                $"{branchingPoint.Hash} " +
                $"TD: {branchingPoint.TotalDifficulty} " +
                $"Processing conditions " +
                $"notFoundTheBranchingPointYet {notFoundTheBranchingPointYet}, " +
                $"hasState: {hasState}, " +
                $"notInForceProcessing: {notInForceProcessing}, ");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceBranchingPoint(BlockHeader? branchingPoint)
        {
            if (branchingPoint is not null && branchingPoint.Hash != _blockTree.Head?.Hash)
            {
                _logger.Trace($"Head block was: {_blockTree.Head?.Header?.ToString(BlockHeader.Format.Short)}");
                _logger.Trace($"Branching from: {branchingPoint.ToString(BlockHeader.Format.Short)}");
            }
            else
            {
                _logger.Trace(branchingPoint is null ? "Setting as genesis block" : $"Adding on top of {branchingPoint.ToString(BlockHeader.Format.Short)}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceProcessingBlock(Block suggestedBlock, Block toBeProcessed)
            => _logger.Trace($"To be processed (of {suggestedBlock.ToString(Block.Format.Short)}) is {toBeProcessed?.ToString(Block.Format.Short)}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceParentSearch(Block toBeProcessed)
            => _logger.Trace($"Finding parent of {toBeProcessed.ToString(Block.Format.Short)}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceParentBlock(Block toBeProcessed)
            => _logger.Trace($"Found parent {toBeProcessed?.ToString(Block.Format.Short)}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceStateRootLookup(Hash256? stateRoot)
            => _logger.Trace($"State root lookup: {stateRoot}");

        [DoesNotReturn, StackTraceHidden]
        static void ThrowMaxBranchSizeReached()
            => throw new InvalidOperationException($"Maximum size of branch reached ({MaxBranchSize}). This is unexpected.");
    }

    [Todo(Improve.Refactor, "This probably can be made conditional (in DEBUG only)")]
    private bool RunSimpleChecksAheadOfProcessing(Block suggestedBlock, ProcessingOptions options)
    {
        /* a bit hacky way to get the invalid branch out of the processing loop */
        if (suggestedBlock.Number != 0 &&
            !_blockTree.IsKnownBlock(suggestedBlock.Number - 1, suggestedBlock.ParentHash))
        {
            if (_logger.IsDebug) LogUnknownParentBlock(suggestedBlock);
            return false;
        }

        if (suggestedBlock.Header.TotalDifficulty is null)
        {
            ThrowUnknownTotalDifficulty(suggestedBlock);
        }

        if (!options.ContainsFlag(ProcessingOptions.NoValidation) && suggestedBlock.Hash is null)
        {
            ThrowUnknownBlockHash(suggestedBlock);
        }

        BlockHeader[] uncles = suggestedBlock.Uncles;
        for (int i = 0; i < uncles.Length; i++)
        {
            if (uncles[i].Hash is null)
            {
                ThrowUnknownUncleHash(suggestedBlock, i);
            }
        }

        return true;

        // Uncommon logging and throws

        [MethodImpl(MethodImplOptions.NoInlining)]
        void LogUnknownParentBlock(Block suggestedBlock)
            => _logger.Debug($"Skipping processing block {suggestedBlock.ToString(Block.Format.FullHashAndNumber)} with unknown parent");

        [DoesNotReturn, StackTraceHidden]
        void ThrowUnknownTotalDifficulty(Block suggestedBlock)
        {
            if (_logger.IsDebug) _logger.Debug($"Skipping processing block {suggestedBlock.ToString(Block.Format.FullHashAndNumber)} without total difficulty");
            throw new InvalidOperationException("Block without total difficulty calculated was suggested for processing");
        }

        [DoesNotReturn, StackTraceHidden]
        void ThrowUnknownBlockHash(Block suggestedBlock)
        {
            if (_logger.IsDebug) _logger.Debug($"Skipping processing block {suggestedBlock.ToString(Block.Format.FullHashAndNumber)} without calculated hash");
            throw new InvalidOperationException("Block hash should be known at this stage if running in a validating mode");
        }

        [DoesNotReturn, StackTraceHidden]
        void ThrowUnknownUncleHash(Block suggestedBlock, int i)
        {
            if (_logger.IsDebug) _logger.Debug($"Skipping processing block {suggestedBlock.ToString(Block.Format.FullHashAndNumber)} with null uncle hash ar {i}");
            throw new InvalidOperationException($"Uncle's {i} hash is null when processing block");
        }
    }

    private void Preprocess(Block block)
    {
        for (int i = 0; i < _preprocessorSteps.Count; i++)
        {
            _preprocessorSteps[i].RecoverData(block);
        }
    }

    [DebuggerDisplay("Root: {Root}, Length: {BlocksToProcess.Count}")]
    private readonly ref struct ProcessingBranch(BlockHeader? baseBlock, ArrayPoolList<Block> blocks)
    {
        public BlockHeader? BaseBlock { get; } = baseBlock;
        public ArrayPoolList<Block> Blocks { get; } = blocks;
        public ArrayPoolList<Block> BlocksToProcess { get; } = new(blocks.Count);

        public void Dispose()
        {
            Blocks.Dispose();
            BlocksToProcess.Dispose();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
