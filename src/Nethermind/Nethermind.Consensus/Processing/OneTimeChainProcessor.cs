// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Exceptions;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// A one-off <see cref="IBlockchainProcessor"/> that runs a single block on its parent's state through
/// its own scope's <see cref="IBranchProcessor"/>, so it never touches the main processing queue.
/// </summary>
/// <remarks>
/// The exclusive lock serializes calls because the wrapped scope's world state and branch processor
/// are single-use. Callers always pass <see cref="ProcessingOptions.ForceProcessing"/>, so no ancestor
/// walk is done: the parent must already have state. The head is never updated: all consumers pass
/// <see cref="ProcessingOptions.DoNotUpdateHead"/>, so the processed block is left for the caller to commit
/// (e.g. by suggesting the sealed block back into the main pipeline).
/// </remarks>
public sealed class OneTimeChainProcessor(
    IBlockTree blockTree,
    IBranchProcessor branchProcessor,
    IReadOnlyList<IBlockPreprocessorStep> preprocessorSteps,
    IStateReader stateReader,
    ILogManager logManager
) : IBlockchainProcessor
{
    private readonly IBranchProcessor _branchProcessor = branchProcessor;
    private readonly IBlockTree _blockTree = blockTree;
    private readonly ILogger _logger = logManager.GetClassLogger<OneTimeChainProcessor>();
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

        BlockHeader? parent = suggestedBlock.IsGenesis ? null : _blockTree.FindParentHeader(suggestedBlock.Header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        if (!suggestedBlock.IsGenesis && parent is null)
        {
            if (_logger.IsDebug) _logger.Debug($"Skipped processing of {suggestedBlock.ToString(Block.Format.FullHashAndNumber)}, parent not found");
            return null;
        }

        _branchBuilder.Preprocess(suggestedBlock);

        using ArrayPoolList<Block> blocks = new(1) { suggestedBlock };
        Block[] processedBlocks;
        try
        {
            processedBlocks = _branchProcessor.Process(parent, blocks, options, tracer, token);
        }
        catch (InvalidBlockException ex)
        {
            if (_logger.IsWarn) _logger.Warn($"Issue processing block {ex.InvalidBlock} {ex}");
            // Env-scope failures must not mutate the canonical tree: a block that is invalid under a
            // producer/trace/debug scope may still be valid on the main processing path.
            return null;
        }

        if (processedBlocks.Length == 0)
        {
            if (_logger.IsDebug) _logger.Debug($"Skipped processing of {suggestedBlock.ToString(Block.Format.FullHashAndNumber)}, nothing was processed");
            return null;
        }

        Block lastProcessed = processedBlocks[^1];
        lastProcessed.Header.TotalDifficulty = suggestedBlock.TotalDifficulty;
        return lastProcessed;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
