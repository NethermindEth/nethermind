// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing.BlockLevelAccessList;

/// <summary>
/// Parallel BAL worker env: executes against a per-tx <see cref="BlockAccessListBasedWorldState"/>
/// backed by a borrowed parent-reader snapshot.
/// </summary>
internal sealed class ParallelBalEnv(
    BlockAccessListBasedWorldState balWorldState,
    TracedAccessWorldState worldState,
    ITransactionProcessor txProcessor,
    ITransactionProcessorAdapter txProcessorAdapter,
    IDisposable? lifetimeScope = null) : IBalProcessingEnv
{
    private readonly BlockAccessListBasedWorldState _balWorldState = balWorldState;
    private ParentReaderLease? _parentReader;

    public TracedAccessWorldState WorldState { get; } = worldState;
    public ITransactionProcessor TxProcessor { get; } = txProcessor;
    public ITransactionProcessorAdapter TxProcessorAdapter { get; } = txProcessorAdapter;

    public void Setup(Block block, BlockExecutionContext blockExecutionContext, uint balIndex, ParentReaderLease? parentReader)
    {
        if (_parentReader is not null) ThrowParentReaderStillAttached();
        if (parentReader is null) ThrowParentReaderUnavailable();

        _parentReader = parentReader;
        WorldState.Clear();
        WorldState.SetIndex(balIndex);
        _balWorldState.SetBlockAccessIndex(balIndex);
        TxProcessorAdapter.SetBlockExecutionContext(new BlockExecutionContext(in blockExecutionContext, parallel: true));
        _balWorldState.SetParentReader(parentReader.WorldState);
        _balWorldState.Setup(block);
    }

    public void ClearParentReader()
    {
        _balWorldState.ClearParentReader();
        _parentReader?.Dispose();
        _parentReader = null;
    }

    public void Dispose()
    {
        // Return the borrowed parent-reader lease, then dispose the owning DI scope (Autofac path).
        ClearParentReader();
        lifetimeScope?.Dispose();
    }

    [DoesNotReturn]
    private static void ThrowParentReaderStillAttached()
        => throw new InvalidOperationException("Previous parent reader was not cleared before reusing this processor.");

    [DoesNotReturn]
    private static void ThrowParentReaderUnavailable()
        => throw new InvalidOperationException("Parallel BAL execution requires a parent-reader source; none configured.");
}
