// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

internal sealed class ProcessingBranchBuilder(IBlockTree blockTree, IStateReader stateReader, IReadOnlyList<IBlockPreprocessorStep> preprocessorSteps, ILogger logger)
{
    private const int MaxBranchSize = 8192;

    private readonly IBlockTree _blockTree = blockTree;
    private readonly IStateReader _stateReader = stateReader;
    private readonly IReadOnlyList<IBlockPreprocessorStep> _preprocessorSteps = preprocessorSteps;
    private readonly ILogger _logger = logger;

    public void PrepareBlocksToProcess(Block suggestedBlock, ProcessingOptions options, ProcessingBranch processingBranch, CancellationToken token)
    {
        ArrayPoolList<Block> blocksToProcess = processingBranch.BlocksToProcess;
        if (options.ContainsFlag(ProcessingOptions.ForceProcessing))
        {
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

    public ProcessingBranch PrepareProcessingBranch(Block suggestedBlock, ProcessingOptions options)
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

            if (!options.ContainsFlag(ProcessingOptions.ForceProcessing))
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
                $" {branchingPoint.Hash} " +
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
    public bool RunSimpleChecksAheadOfProcessing(Block suggestedBlock, ProcessingOptions options)
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

    public void Preprocess(Block block)
    {
        for (int i = 0; i < _preprocessorSteps.Count; i++)
        {
            _preprocessorSteps[i].RecoverData(block);
        }
    }
}

[DebuggerDisplay("Root: {Root}, Length: {BlocksToProcess.Count}")]
internal readonly ref struct ProcessingBranch(BlockHeader? baseBlock, ArrayPoolList<Block> blocks)
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
