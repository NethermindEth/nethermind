// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.State.Flat.History.Changesets;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Init.Steps;

/// <summary>Re-executes a canonical block on a world state of its own, the way a historical trace does, so that
/// nothing it touches is shared with the processing of the chain.</summary>
public class ProcessingHistoryBlockExecutor(
    IBlockTree blockTree,
    IOverridableEnv<ProcessingHistoryBlockExecutor.Components> processingEnv) : IHistoryBlockExecutor
{
    public bool TryExecute(ulong block, IBlockTracer tracer, CancellationToken cancellationToken)
    {
        Block? toExecute = blockTree.FindBlock(block, BlockTreeLookupOptions.RequireCanonical);
        if (toExecute?.Header.ParentHash is null) return false;

        BlockHeader? parent = blockTree.FindHeader(toExecute.Header.ParentHash, BlockTreeLookupOptions.None);
        if (parent is null) return false;

        using Scope<Components> scope = processingEnv.BuildAndOverride(parent, null);
        scope.Component.BlockchainProcessor.Process(toExecute, ProcessingOptions.Trace, tracer, cancellationToken);
        return true;
    }

    public record Components(IWorldState WorldState, BlockchainProcessorFacade BlockchainProcessor);
}
