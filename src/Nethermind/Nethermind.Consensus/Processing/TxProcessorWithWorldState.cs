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

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Bundles a tx processor with its world states (BAL-backed in parallel mode, plain in sequential)
/// and its adapter. The default <see cref="IBalProcessingEnv"/> implementation.
/// </summary>
internal class TxProcessorWithWorldState : IBalProcessingEnv
{
    public TracedAccessWorldState WorldState { get; }
    public ITransactionProcessor TxProcessor { get; }
    public ITransactionProcessorAdapter TxProcessorAdapter { get; }
    private readonly BlockAccessListBasedWorldState? _balWorldState;
    private readonly bool _parallel;
    private ParentReaderLease? _parentReader;

    public TxProcessorWithWorldState(
        bool parallel,
        IBlockhashProvider blockHashProvider,
        ISpecProvider specProvider,
        IWorldState stateProvider,
        ILogManager logManager,
        ITransactionProcessorFactory txProcessorFactory,
        CodeInfoRepositoryFactory codeInfoRepositoryFactory)
    {
        _parallel = parallel;
        VirtualMachine virtualMachine = new(blockHashProvider, specProvider, logManager);
        IWorldState worldState = stateProvider;
        if (parallel)
        {
            _balWorldState = new BlockAccessListBasedWorldState(stateProvider, logManager);
            worldState = _balWorldState;
        }
        WorldState = new TracedAccessWorldState(worldState, parallel);
        ICodeInfoRepository codeInfoRepository = codeInfoRepositoryFactory(WorldState);
        TxProcessor = txProcessorFactory.Create(BlobBaseFeeCalculator.Instance, specProvider, WorldState, virtualMachine, codeInfoRepository, logManager);
        TxProcessorAdapter = new ExecuteTransactionProcessorAdapter(TxProcessor);
    }

    public void Setup(Block block, BlockExecutionContext blockExecutionContext, uint balIndex, ParentReaderLease? parentReader)
    {
        if (_parentReader is not null) ThrowParentReaderStillAttached();

        _parentReader = parentReader;
        WorldState.Clear();
        WorldState.SetIndex(balIndex);
        _balWorldState?.SetBlockAccessIndex(balIndex);
        TxProcessorAdapter.SetBlockExecutionContext(
            _parallel ? new BlockExecutionContext(in blockExecutionContext, parallel: true) : blockExecutionContext);
        if (_balWorldState is not null)
        {
            if (parentReader is null) ThrowParentReaderUnavailable();
            _balWorldState.SetParentReader(parentReader.WorldState);
            _balWorldState.Setup(block);
        }
    }

    public void ClearParentReader()
    {
        _balWorldState?.ClearParentReader();
        _parentReader?.Dispose();
        _parentReader = null;
    }

    [DoesNotReturn]
    private static void ThrowParentReaderStillAttached()
        => throw new InvalidOperationException("Previous parent reader was not cleared before reusing this processor.");

    [DoesNotReturn]
    private static void ThrowParentReaderUnavailable()
        => throw new InvalidOperationException("Parallel BAL execution requires a parent-reader source; none configured.");
}
