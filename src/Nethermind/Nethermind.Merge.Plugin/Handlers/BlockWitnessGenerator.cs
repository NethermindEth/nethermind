// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>Builds the execution witness of a block by re-executing it on its parent state.</summary>
public sealed class BlockWitnessGenerator(
    IBlockFinder blockFinder,
    IStateReader stateReader,
    IReadOnlyList<IBlockPreprocessorStep> preprocessorSteps,
    Lazy<IWitnessGeneratingBlockProcessingEnvFactory> witnessEnvFactory)
{
    /// <returns><c>null</c> when the parent is not in the block tree or its state is not available.</returns>
    /// <remarks>The block need not be in the block tree, so a payload still being built qualifies.</remarks>
    public Witness? Generate(Block block, CancellationToken token = default)
    {
        BlockHeader? parent = blockFinder.FindHeader(block.ParentHash!);
        if (parent is null || !stateReader.HasStateForBlock(parent)) return null;

        // The collector bypasses the processing pipeline, so run its preprocessing (sender recovery) here.
        for (int i = 0; i < preprocessorSteps.Count; i++)
        {
            preprocessorSteps[i].RecoverData(block);
        }

        using IWitnessGeneratingBlockProcessingEnvScope scope = witnessEnvFactory.Value.CreateScope();
        return scope.Env.CreateExistingBlockWitnessCollector().GetWitnessForExistingBlock(parent, block, token);
    }
}
