// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// The <see cref="IBlockchainProcessor"/> of the block producer env that builds on the main (global) world state.
/// Like <see cref="BlockchainProcessor"/> it walks back to reprocess ancestors lacking state, but it never
/// touches the main processing queue, never updates the head and processes every block it is handed
/// regardless of how it compares to the head.
/// </summary>
/// <remarks>
/// The exclusive lock serializes calls because the wrapped scope's world state and branch processor
/// are single-use. All consumers pass <see cref="ProcessingOptions.DoNotUpdateHead"/>, so the processed
/// block is left for the caller to commit (e.g. via <see cref="IBlockTree.TryUpdateMainChain"/>).
/// </remarks>
public sealed class MainStateBlockBuildingChainProcessor(
    IBlockTree blockTree,
    IBranchProcessor branchProcessor,
    IReadOnlyList<IBlockPreprocessorStep> preprocessorSteps,
    IStateReader stateReader,
    ILogManager logManager
) : IBlockchainProcessor
{
    private readonly IBranchProcessor _branchProcessor = branchProcessor;
    private readonly ILogger _logger = logManager.GetClassLogger<MainStateBlockBuildingChainProcessor>();
    private readonly ProcessingBranchBuilder _branchBuilder = new(blockTree, stateReader, preprocessorSteps, logManager.GetClassLogger<ProcessingBranchBuilder>());
    private readonly Lock _lock = new();

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
            // Env-scope failures must not mutate the canonical tree: a block that is invalid under a
            // producer/trace/debug scope may still be valid on the main processing path.
            processedBlocks = null;
        }

        return processedBlocks;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
