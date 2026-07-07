// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;
using Nethermind.Consensus.Processing.BlockLevelAccessList;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Manual (non-DI) construction of a <see cref="BlockAccessListManager"/>: builds the tx-processor
/// pool managers (over a hand-built <see cref="ManualMainnetBalProcessingEnvFactory"/>) and wires
/// them into a manager. The DI path injects the managers directly; this exists for the
/// stateless/witness envs and tests that construct the manager outside the container.
/// </summary>
public static class ManualBlockAccessListManagerFactory
{
    public static BlockAccessListManager Create(
        IWorldState stateProvider,
        ISpecProvider specProvider,
        IBlockhashProvider blockHashProvider,
        ILogManager logManager,
        IBlocksConfig blocksConfig,
        IWithdrawalProcessorFactory withdrawalProcessorFactory,
        CodeInfoRepositoryFactory codeInfoRepositoryFactory,
        PrewarmerEnvFactory? prewarmerEnvFactory = null,
        PreBlockCaches? preBlockCaches = null,
        IReadOnlyTxProcessingEnvFactory? readOnlyTxProcessingEnvFactory = null)
    {
        ManualMainnetBalProcessingEnvFactory envFactory = new(
            blockHashProvider, specProvider, stateProvider, logManager, codeInfoRepositoryFactory, withdrawalProcessorFactory);
        return new BlockAccessListManager(
            stateProvider,
            logManager,
            blocksConfig,
            new Lazy<IParallelBalEnvManager>(() => new ParallelBalEnvManager(envFactory, prewarmerEnvFactory, preBlockCaches, readOnlyTxProcessingEnvFactory)),
            new Lazy<ISequentialBalEnvManager>(() => new SequentialBalEnvManager(envFactory)),
            prewarmerEnvFactory,
            preBlockCaches,
            readOnlyTxProcessingEnvFactory);
    }

    /// <summary>
    /// Manual <see cref="IBalProcessingEnvFactory"/>: builds all worker components (virtual machine,
    /// traced world state, tx processor and adapter) by hand and hands them to a
    /// <see cref="ParallelBalEnv"/> or <see cref="SequentialBalEnv"/>. The DI path uses
    /// <see cref="AutofacBalProcessingEnvFactory"/> instead.
    /// </summary>
    private sealed class ManualMainnetBalProcessingEnvFactory(
        IBlockhashProvider blockHashProvider,
        ISpecProvider specProvider,
        IWorldState stateProvider,
        ILogManager logManager,
        CodeInfoRepositoryFactory codeInfoRepositoryFactory,
        IWithdrawalProcessorFactory withdrawalProcessorFactory) : IBalProcessingEnvFactory
    {
        public IBalProcessingEnv Create(bool parallel)
        {
            if (parallel)
            {
                BlockAccessListBasedWorldState balWorldState = new(stateProvider, logManager);
                TracedAccessWorldState worldState = new(balWorldState, parallel: true);
                VirtualMachine virtualMachine = new(blockHashProvider, specProvider, logManager);
                ICodeInfoRepository codeInfoRepository = codeInfoRepositoryFactory(worldState);
                ITransactionProcessor processor = new TransactionProcessor<EthereumGasPolicy>(BlobBaseFeeCalculator.Instance, specProvider, worldState, virtualMachine, codeInfoRepository, logManager);
                return new ParallelBalEnv(balWorldState, worldState, processor, new ExecuteTransactionProcessorAdapter(processor), withdrawalProcessorFactory.Create(worldState, processor));
            }
            else
            {
                TracedAccessWorldState worldState = new(stateProvider, parallel: false);
                VirtualMachine virtualMachine = new(blockHashProvider, specProvider, logManager);
                ICodeInfoRepository codeInfoRepository = codeInfoRepositoryFactory(worldState);
                ITransactionProcessor processor = new TransactionProcessor<EthereumGasPolicy>(BlobBaseFeeCalculator.Instance, specProvider, worldState, virtualMachine, codeInfoRepository, logManager);
                return new SequentialBalEnv(worldState, processor, new ExecuteTransactionProcessorAdapter(processor), withdrawalProcessorFactory.Create(worldState, processor));
            }
        }
    }
}
