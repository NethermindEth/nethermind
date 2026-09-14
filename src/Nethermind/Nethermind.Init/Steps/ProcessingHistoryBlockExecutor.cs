// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Autofac;
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
/// nothing it touches is shared with the processing of the chain or with another executor.</summary>
public sealed class ProcessingHistoryBlockExecutor(
    IBlockTree blockTree,
    IOverridableEnv<ProcessingHistoryBlockExecutor.Components> processingEnv,
    ILifetimeScope scope) : IHistoryBlockExecutor
{
    public bool TryExecute(ulong block, IBlockTracer tracer, CancellationToken cancellationToken)
    {
        Block? toExecute = blockTree.FindBlock(block, BlockTreeLookupOptions.RequireCanonical);
        if (toExecute?.Header.ParentHash is null) return false;

        BlockHeader? parent = blockTree.FindHeader(toExecute.Header.ParentHash, BlockTreeLookupOptions.None);
        if (parent is null) return false;

        using Scope<Components> processing = processingEnv.BuildAndOverride(parent, null);
        processing.Component.BlockchainProcessor.Process(toExecute, ProcessingOptions.Trace, tracer, cancellationToken);
        return true;
    }

    public void Dispose() => scope.Dispose();

    public record Components(IWorldState WorldState, BlockchainProcessorFacade BlockchainProcessor);
}

public class ProcessingHistoryBlockExecutorFactory(IBlockTree blockTree, ILifetimeScope parent) : IHistoryBlockExecutorFactory
{
    public IHistoryBlockExecutor Create()
    {
        ILifetimeScope scope = parent.BeginLifetimeScope();
        return new ProcessingHistoryBlockExecutor(blockTree, scope.Resolve<IOverridableEnv<ProcessingHistoryBlockExecutor.Components>>(), scope);
    }
}
