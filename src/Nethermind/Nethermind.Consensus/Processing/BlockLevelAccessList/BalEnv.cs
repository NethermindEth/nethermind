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
/// Shared base for the BAL processing environments: bundles a tx processor with its traced world
/// state and adapter. <see cref="ParallelBalEnv"/> and <see cref="SequentialBalEnv"/> differ only
/// in the world-state backing and per-tx setup.
/// </summary>
internal abstract class BalEnv : IBalProcessingEnv
{
    public TracedAccessWorldState WorldState { get; }
    public ITransactionProcessor TxProcessor { get; }
    public ITransactionProcessorAdapter TxProcessorAdapter { get; }

    protected BalEnv(
        IWorldState worldState,
        bool parallel,
        IBlockhashProvider blockHashProvider,
        ISpecProvider specProvider,
        ILogManager logManager,
        ITransactionProcessorFactory txProcessorFactory,
        CodeInfoRepositoryFactory codeInfoRepositoryFactory)
    {
        VirtualMachine virtualMachine = new(blockHashProvider, specProvider, logManager);
        WorldState = new TracedAccessWorldState(worldState, parallel);
        ICodeInfoRepository codeInfoRepository = codeInfoRepositoryFactory(WorldState);
        TxProcessor = txProcessorFactory.Create(BlobBaseFeeCalculator.Instance, specProvider, WorldState, virtualMachine, codeInfoRepository, logManager);
        TxProcessorAdapter = new ExecuteTransactionProcessorAdapter(TxProcessor);
    }

    public abstract void Setup(Block block, BlockExecutionContext blockExecutionContext, uint balIndex, ParentReaderLease? parentReader);
    public virtual void ClearParentReader() { }
    public virtual void Dispose() { }
}
