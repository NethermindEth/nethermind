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
/// Parallel BAL worker env: bundles a tx processor with its traced world state (backed by a per-tx
/// <see cref="BlockAccessListBasedWorldState"/> reading from a borrowed parent-reader snapshot) and
/// adapter.
/// </summary>
internal sealed class ParallelBalEnv : IBalProcessingEnv
{
    public TracedAccessWorldState WorldState { get; }
    public ITransactionProcessor TxProcessor { get; }
    public ITransactionProcessorAdapter TxProcessorAdapter { get; }
    private readonly BlockAccessListBasedWorldState _balWorldState;
    private ParentReaderLease? _parentReader;

    public ParallelBalEnv(
        IBlockhashProvider blockHashProvider,
        ISpecProvider specProvider,
        IWorldState stateProvider,
        ILogManager logManager,
        ITransactionProcessorFactory txProcessorFactory,
        CodeInfoRepositoryFactory codeInfoRepositoryFactory)
    {
        VirtualMachine virtualMachine = new(blockHashProvider, specProvider, logManager);
        _balWorldState = new BlockAccessListBasedWorldState(stateProvider, logManager);
        WorldState = new TracedAccessWorldState(_balWorldState, parallel: true);
        ICodeInfoRepository codeInfoRepository = codeInfoRepositoryFactory(WorldState);
        TxProcessor = txProcessorFactory.Create(BlobBaseFeeCalculator.Instance, specProvider, WorldState, virtualMachine, codeInfoRepository, logManager);
        TxProcessorAdapter = new ExecuteTransactionProcessorAdapter(TxProcessor);
    }

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

    // The only owned disposable is the borrowed parent-reader lease; ClearParentReader returns it.
    public void Dispose() => ClearParentReader();

    [DoesNotReturn]
    private static void ThrowParentReaderStillAttached()
        => throw new InvalidOperationException("Previous parent reader was not cleared before reusing this processor.");

    [DoesNotReturn]
    private static void ThrowParentReaderUnavailable()
        => throw new InvalidOperationException("Parallel BAL execution requires a parent-reader source; none configured.");
}
