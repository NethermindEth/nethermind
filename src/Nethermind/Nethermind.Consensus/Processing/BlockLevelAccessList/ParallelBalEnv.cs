// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing.BlockLevelAccessList;

/// <summary>
/// Parallel BAL worker env: executes against a per-tx <see cref="BlockAccessListBasedWorldState"/>
/// backed by a borrowed parent-reader snapshot.
/// </summary>
internal sealed class ParallelBalEnv : BalEnv
{
    private readonly BlockAccessListBasedWorldState _balWorldState;
    private ParentReaderLease? _parentReader;

    public ParallelBalEnv(
        IBlockhashProvider blockHashProvider,
        ISpecProvider specProvider,
        IWorldState stateProvider,
        ILogManager logManager,
        ITransactionProcessorFactory txProcessorFactory,
        CodeInfoRepositoryFactory codeInfoRepositoryFactory)
        : this(new BlockAccessListBasedWorldState(stateProvider, logManager),
            blockHashProvider, specProvider, logManager, txProcessorFactory, codeInfoRepositoryFactory)
    { }

    // Delegating ctor so the BAL-backed world state can be handed to the base (as the traced
    // state's inner backing) and kept as a typed field for per-tx setup, without re-creating it.
    private ParallelBalEnv(
        BlockAccessListBasedWorldState balWorldState,
        IBlockhashProvider blockHashProvider,
        ISpecProvider specProvider,
        ILogManager logManager,
        ITransactionProcessorFactory txProcessorFactory,
        CodeInfoRepositoryFactory codeInfoRepositoryFactory)
        : base(balWorldState, parallel: true, blockHashProvider, specProvider, logManager, txProcessorFactory, codeInfoRepositoryFactory)
        => _balWorldState = balWorldState;

    public override void Setup(Block block, BlockExecutionContext blockExecutionContext, uint balIndex, ParentReaderLease? parentReader)
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

    public override void ClearParentReader()
    {
        _balWorldState.ClearParentReader();
        _parentReader?.Dispose();
        _parentReader = null;
    }

    // The only owned disposable is the borrowed parent-reader lease; ClearParentReader returns it.
    public override void Dispose() => ClearParentReader();

    [DoesNotReturn]
    private static void ThrowParentReaderStillAttached()
        => throw new InvalidOperationException("Previous parent reader was not cleared before reusing this processor.");

    [DoesNotReturn]
    private static void ThrowParentReaderUnavailable()
        => throw new InvalidOperationException("Parallel BAL execution requires a parent-reader source; none configured.");
}
