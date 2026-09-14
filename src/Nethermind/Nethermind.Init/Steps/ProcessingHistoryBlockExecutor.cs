// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Evm.Tracing;
using Nethermind.State.Flat.History.Changesets;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Init.Steps;

/// <summary>Re-executes a canonical block on a world state of its own, the way a historical trace does, so that
/// nothing it touches is shared with the processing of the chain or with another executor.</summary>
public sealed class ProcessingHistoryBlockExecutor(
    IBlockTree blockTree,
    IOverridableEnv processingEnv,
    BlockchainProcessorFacade processor,
    ILifetimeScope scope) : IHistoryBlockExecutor
{
    public bool TryExecute(ulong block, IBlockTracer tracer, CancellationToken cancellationToken)
    {
        Block? toExecute = blockTree.FindBlock(block, BlockTreeLookupOptions.RequireCanonical);
        if (toExecute?.Header.ParentHash is null) return false;

        BlockHeader? parent = blockTree.FindHeader(toExecute.Header.ParentHash, BlockTreeLookupOptions.None);
        if (parent is null) return false;

        using IDisposable processing = processingEnv.BuildAndOverride(parent);
        processor.Process(toExecute, TraceProcessingOptions.ReadOnlyReplay, tracer, cancellationToken);
        return true;
    }

    public void Dispose()
    {
        scope.Dispose();
        (processingEnv as IDisposable)?.Dispose();
    }
}

/// <summary>Builds each executor its own processing scope, shaped like the one the debug RPC module traces in.</summary>
public class ProcessingHistoryBlockExecutorFactory(
    IBlockTree blockTree,
    IOverridableEnvFactory envFactory,
    ILifetimeScope rootLifetimeScope,
    IBlockValidationModule[] validationModules) : IHistoryBlockExecutorFactory
{
    public IHistoryBlockExecutor Create()
    {
        IOverridableEnv env = envFactory.Create();
        ILifetimeScope scope = rootLifetimeScope.BeginLifetimeScope(builder => builder
            .AddModule(validationModules)
            .AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>()
            .AddScoped<BlockchainProcessor.Options>(BlockchainProcessor.Options.NoReceipts)
            .AddModule(env));
        return new ProcessingHistoryBlockExecutor(blockTree, env, scope.Resolve<BlockchainProcessorFacade>(), scope);
    }
}
