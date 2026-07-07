// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing.BlockLevelAccessList;

/// <summary>
/// Manual (non-DI) <see cref="IBalProcessingEnvFactory"/>: builds all worker components (virtual
/// machine, traced world state, tx processor and adapter) by hand and hands them to a
/// <see cref="ParallelBalEnv"/> or <see cref="SequentialBalEnv"/>. Used by the stateless/witness
/// envs and tests that construct outside the container; the DI path uses
/// <see cref="AutofacBalProcessingEnvFactory"/>.
/// </summary>
public sealed class ManualMainnetBalProcessingEnvFactory(
    IBlockhashProvider blockHashProvider,
    ISpecProvider specProvider,
    IWorldState stateProvider,
    ILogManager logManager,
    ITransactionProcessorFactory txProcessorFactory,
    CodeInfoRepositoryFactory codeInfoRepositoryFactory) : IBalProcessingEnvFactory
{
    public IBalProcessingEnv Create(bool parallel)
    {
        if (parallel)
        {
            BlockAccessListBasedWorldState balWorldState = new(stateProvider, logManager);
            TracedAccessWorldState worldState = new(balWorldState, parallel: true);
            (ITransactionProcessor processor, ITransactionProcessorAdapter adapter) = CreateProcessor(worldState);
            return new ParallelBalEnv(balWorldState, worldState, processor, adapter);
        }
        else
        {
            TracedAccessWorldState worldState = new(stateProvider, parallel: false);
            (ITransactionProcessor processor, ITransactionProcessorAdapter adapter) = CreateProcessor(worldState);
            return new SequentialBalEnv(worldState, processor, adapter);
        }
    }

    private (ITransactionProcessor Processor, ITransactionProcessorAdapter Adapter) CreateProcessor(TracedAccessWorldState worldState)
    {
        VirtualMachine virtualMachine = new(blockHashProvider, specProvider, logManager);
        ICodeInfoRepository codeInfoRepository = codeInfoRepositoryFactory(worldState);
        ITransactionProcessor processor = txProcessorFactory.Create(BlobBaseFeeCalculator.Instance, specProvider, worldState, virtualMachine, codeInfoRepository, logManager);
        return (processor, new ExecuteTransactionProcessorAdapter(processor));
    }
}
