// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing.BlockLevelAccessList;

/// <summary>
/// Sequential BAL env: bundles a tx processor with its traced world state (backed by the mutable
/// state provider directly) and adapter.
/// </summary>
internal sealed class SequentialBalEnv : IBalProcessingEnv
{
    public TracedAccessWorldState WorldState { get; }
    public ITransactionProcessor TxProcessor { get; }
    public ITransactionProcessorAdapter TxProcessorAdapter { get; }

    public SequentialBalEnv(
        IBlockhashProvider blockHashProvider,
        ISpecProvider specProvider,
        IWorldState stateProvider,
        ILogManager logManager,
        ITransactionProcessorFactory txProcessorFactory,
        CodeInfoRepositoryFactory codeInfoRepositoryFactory)
    {
        VirtualMachine virtualMachine = new(blockHashProvider, specProvider, logManager);
        WorldState = new TracedAccessWorldState(stateProvider, parallel: false);
        ICodeInfoRepository codeInfoRepository = codeInfoRepositoryFactory(WorldState);
        TxProcessor = txProcessorFactory.Create(BlobBaseFeeCalculator.Instance, specProvider, WorldState, virtualMachine, codeInfoRepository, logManager);
        TxProcessorAdapter = new ExecuteTransactionProcessorAdapter(TxProcessor);
    }

    public void Setup(Block block, BlockExecutionContext blockExecutionContext, uint balIndex, ParentReaderLease? parentReader)
    {
        WorldState.Clear();
        WorldState.SetIndex(balIndex);
        TxProcessorAdapter.SetBlockExecutionContext(blockExecutionContext);
    }

    public void ClearParentReader() { }

    public void Dispose() { }
}
