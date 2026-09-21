// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
            // The run keeps one state open on the block just executed, so the next block must be its child. If the
            // canonical chain moved between the two lookups, end the run rather than execute a sibling's child here.
            Block? candidate = executor.FindCanonical((ulong)block.Number + 1);
            _next = candidate?.Header.ParentHash == block.Hash ? candidate : null;
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
    private ForkBoundary? _boundary;

    private sealed record ForkBoundary(ulong LastSupported, Hash256 SupportedHash, Hash256 UnsupportedHash);

    public ulong? GetLastSupportedBlock(ulong lowerBound, ulong upperBound)
    {
        if (lowerBound > upperBound) return null;
        BlockHeader? last = blockTree.FindHeader(upperBound, BlockTreeLookupOptions.RequireCanonical);
        if (last is null) return null;
        if (!specProvider.GetSpec(last).BlockLevelAccessListsEnabled) return upperBound;

        BlockHeader? first = blockTree.FindHeader(lowerBound, BlockTreeLookupOptions.RequireCanonical);
        if (first is null || specProvider.GetSpec(first).BlockLevelAccessListsEnabled) return null;

        ForkBoundary? boundary = Volatile.Read(ref _boundary);
        if (boundary is not null && boundary.LastSupported < upperBound)
        {
            BlockHeader? before = blockTree.FindHeader(boundary.LastSupported, BlockTreeLookupOptions.RequireCanonical);
            BlockHeader? after = blockTree.FindHeader(boundary.LastSupported + 1, BlockTreeLookupOptions.RequireCanonical);
            if (before?.Hash == boundary.SupportedHash && after?.Hash == boundary.UnsupportedHash
                && !specProvider.GetSpec(before).BlockLevelAccessListsEnabled && specProvider.GetSpec(after).BlockLevelAccessListsEnabled)
                return lowerBound <= boundary.LastSupported ? boundary.LastSupported : null;
            Interlocked.CompareExchange(ref _boundary, null, boundary);
        }

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
        if (supported is { } number)
        {
            BlockHeader? before = blockTree.FindHeader(number, BlockTreeLookupOptions.RequireCanonical);
            BlockHeader? after = blockTree.FindHeader(number + 1, BlockTreeLookupOptions.RequireCanonical);
            if (before?.Hash is { } supportedHash && after?.Hash is { } unsupportedHash
                && !specProvider.GetSpec(before).BlockLevelAccessListsEnabled && specProvider.GetSpec(after).BlockLevelAccessListsEnabled)
                Volatile.Write(ref _boundary, new ForkBoundary(number, supportedHash, unsupportedHash));
        }
        return supported;
    }

    public Hash256? GetCanonicalHash(ulong block) => blockTree.FindHeader(block, BlockTreeLookupOptions.RequireCanonical)?.Hash;

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
