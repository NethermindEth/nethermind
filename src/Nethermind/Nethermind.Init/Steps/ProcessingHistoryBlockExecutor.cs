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
    private readonly BlockchainProcessorFacade _processor = processor;

    public bool TryExecute(ulong block, IBlockTracer tracer, CancellationToken cancellationToken)
    {
        using IHistoryBlockRun? run = BeginRun(block);
        return run is not null && run.TryExecuteNext(tracer, cancellationToken);
    }

    public IHistoryBlockRun? BeginRun(ulong firstBlock)
    {
        Block? first = FindCanonical(firstBlock);
        if (first?.Header.ParentHash is null) return null;

        BlockHeader? parent = blockTree.FindHeader(first.Header.ParentHash, BlockTreeLookupOptions.None);
        return parent is null ? null : new Run(this, processingEnv.BuildAndOverride(parent), first);
    }

    private Block? FindCanonical(ulong block) => blockTree.FindBlock(block, BlockTreeLookupOptions.RequireCanonical);

    private sealed class Run(ProcessingHistoryBlockExecutor executor, IDisposable state, Block first) : IHistoryBlockRun
    {
        private Block? _next = first;

        public bool TryExecuteNext(IBlockTracer tracer, CancellationToken cancellationToken)
        {
            Block? block = _next;
            if (block is null) return false;

            executor._processor.Process(block, TraceProcessingOptions.ReadOnlyReplay | ProcessingOptions.ForceSequentialBlockAccessList, tracer, cancellationToken);
            _next = executor.FindCanonical((ulong)block.Number + 1);
            return true;
        }

        public void Dispose() => state.Dispose();
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
