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
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.State.Flat.History.Changesets;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Init.Steps;

/// <summary>Re-executes a canonical block on a world state of its own, the way a historical trace does, so that
/// nothing it touches is shared with the processing of the chain or with another executor.</summary>
public sealed class ProcessingHistoryBlockExecutor(
    IBlockTree blockTree,
    ISpecProvider specProvider,
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

    private Block? FindCanonical(ulong block)
    {
        Block? candidate = blockTree.FindBlock(block, BlockTreeLookupOptions.RequireCanonical);
        return candidate is not null && !specProvider.GetSpec(candidate.Header).BlockLevelAccessListsEnabled ? candidate : null;
    }

    private sealed class Run(ProcessingHistoryBlockExecutor executor, IDisposable state, Block first) : IHistoryBlockRun
    {
        private Block? _next = first;

        public bool TryExecuteNext(IBlockTracer tracer, CancellationToken cancellationToken)
        {
            Block? block = _next;
            if (block is null) return false;

            Block isolated = block.WithReplacedHeader(block.Header.Clone());
            try
            {
                executor._processor.Process(isolated, TraceProcessingOptions.ReadOnlyReplay | ProcessingOptions.ForceSequentialBlockAccessList, tracer, cancellationToken);
            }
            finally
            {
                isolated.DisposeAccountChanges();
            }
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
public sealed class ProcessingHistoryBlockExecutorFactory(
    IBlockTree blockTree,
    ISpecProvider specProvider,
    IOverridableEnvFactory envFactory,
    ILifetimeScope rootLifetimeScope,
    IBlockValidationModule[] validationModules) : IHistoryBlockExecutorFactory
{
    public ulong? GetLastSupportedBlock(ulong lowerBound, ulong upperBound)
    {
        if (lowerBound > upperBound) return null;
        BlockHeader? last = blockTree.FindHeader(upperBound, BlockTreeLookupOptions.RequireCanonical);
        if (last is null) return null;
        if (!specProvider.GetSpec(last).BlockLevelAccessListsEnabled) return upperBound;

        ulong lower = lowerBound;
        ulong upper = upperBound;
        ulong? supported = null;
        while (lower <= upper)
        {
            ulong middle = lower + (upper - lower) / 2;
            BlockHeader? header = blockTree.FindHeader(middle, BlockTreeLookupOptions.RequireCanonical);
            if (header is null) return null;
            if (specProvider.GetSpec(header).BlockLevelAccessListsEnabled)
            {
                if (middle == 0) break;
                upper = middle - 1;
            }
            else
            {
                supported = middle;
                if (middle == upperBound) break;
                lower = middle + 1;
            }
        }
        return supported;
    }

    public IHistoryBlockExecutor Create()
    {
        IOverridableEnv env = envFactory.Create();
        ILifetimeScope? scope = null;
        try
        {
            scope = rootLifetimeScope.BeginLifetimeScope(builder => builder
                .AddModule(validationModules)
                .AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>()
                .AddScoped<BlockchainProcessor.Options>(BlockchainProcessor.Options.NoReceipts)
                .AddModule(env));
            return new ProcessingHistoryBlockExecutor(blockTree, specProvider, env, scope.Resolve<BlockchainProcessorFacade>(), scope);
        }
        catch
        {
            scope?.Dispose();
            (env as IDisposable)?.Dispose();
            throw;
        }
    }
}
