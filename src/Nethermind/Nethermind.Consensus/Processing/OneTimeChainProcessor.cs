// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
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
    IBlockTree blockTree,
    IBranchProcessor branchProcessor,
    IReadOnlyList<IBlockPreprocessorStep> preprocessorSteps,
    IStateReader stateReader,
    ILogManager logManager,
    IEnumerable<IBlockTracer>? blockTracers = null
) : IBlockchainProcessor
{
    private readonly IBranchProcessor _branchProcessor = branchProcessor;
    private readonly IBlockTree _blockTree = blockTree;
    private readonly ILogger _logger = logManager.GetClassLogger<OneTimeChainProcessor>();
    private readonly ProcessingBranchBuilder _branchBuilder = new(blockTree, stateReader, preprocessorSteps, logManager.GetClassLogger<ProcessingBranchBuilder>());
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
        if (!_branchBuilder.RunSimpleChecksAheadOfProcessing(suggestedBlock, options))
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

        using ProcessingBranch processingBranch = _branchBuilder.PrepareProcessingBranch(suggestedBlock, options);
        _branchBuilder.PrepareBlocksToProcess(suggestedBlock, options, processingBranch, token);

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

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
