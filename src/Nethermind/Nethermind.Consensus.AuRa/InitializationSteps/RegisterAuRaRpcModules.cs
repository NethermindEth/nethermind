// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa.InitializationSteps;

public class AuRaTraceModuleFactory(IWorldStateManager worldStateManager, Func<ICodeInfoRepository> codeInfoRepositoryFunc, ILifetimeScope rootLifetimeScope) : TraceModuleFactory(worldStateManager, codeInfoRepositoryFunc, rootLifetimeScope)
{
    protected override ContainerBuilder ConfigureCommonBlockProcessing<T>(ContainerBuilder builder)
    {
        // Screw it! aura will just construct things on its own.
        return base.ConfigureCommonBlockProcessing<T>(builder)
            .AddScoped<IReadOnlyTxProcessingScope, ITransactionProcessor, IWorldState>((txP, worldState) => new ReadOnlyTxProcessingScope(txP, worldState, Keccak.EmptyTreeHash))
            .AddScoped<ReadOnlyChainProcessingEnv, AuRaReadOnlyChainProcessingEnv>()
            .AddScoped<IBlockProcessor, ReadOnlyChainProcessingEnv>((env) => env.BlockProcessor);
    }
}

public class AuRaReadOnlyChainProcessingEnv(
    AuRaChainSpecEngineParameters parameters,
    IAuraConfig auraConfig,
    IBlocksConfig blocksConfig,
    IAbiEncoder abiEncoder,
    IReadOnlyTxProcessingEnvFactory envFactory,
    AuRaContractGasLimitOverride.Cache gasLimitOverrideCache,
    IReadOnlyTxProcessingScope scope,
    IBlockValidator blockValidator,
    IBlockPreprocessorStep recoveryStep,
    IRewardCalculator rewardCalculator,
    IReceiptStorage receiptStorage,
    ISpecProvider specProvider,
    IBlockTree blockTree,
    IStateReader stateReader,
    ILogManager logManager,
    IBlockProcessor.IBlockTransactionsExecutor? blockTransactionsExecutor,
    IAuRaBlockProcessorFactory factory)
    : ReadOnlyChainProcessingEnv(scope, blockValidator, recoveryStep, rewardCalculator, receiptStorage,
        specProvider, blockTree, stateReader, logManager, blockTransactionsExecutor)
{
    private readonly ISpecProvider _specProvider = specProvider;
    private readonly ILogManager _logManager = logManager;

    protected override IBlockProcessor CreateBlockProcessor(IReadOnlyTxProcessingScope scope, IBlockTree blockTree,
        IBlockValidator blockValidator, IRewardCalculator rewardCalculator, IReceiptStorage receiptStorage,
        ISpecProvider specProvider, ILogManager logManager, IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor)
    {
        ITxFilter auRaTxFilter = new ServiceTxFilter(specProvider);

        IDictionary<long, IDictionary<Address, byte[]>> rewriteBytecode = parameters.RewriteBytecode;
        ContractRewriter? contractRewriter = rewriteBytecode?.Count > 0 ? new ContractRewriter(rewriteBytecode) : null;

        return factory.Create(
            specProvider!,
            blockValidator!,
            rewardCalculator,
            transactionsExecutor,
            scope.WorldState,
            receiptStorage,
            new BeaconBlockRootHandler(scope.TransactionProcessor, scope.WorldState),
            logManager,
            blockTree,
            NullWithdrawalProcessor.Instance,
            new ExecutionRequestsProcessor(scope.TransactionProcessor),
            auRaValidator: null,
            auRaTxFilter,
            GetGasLimitCalculator(),
            contractRewriter
        );
    }

    private AuRaContractGasLimitOverride? GetGasLimitCalculator()
    {
        var blockGasLimitContractTransitions = parameters.BlockGasLimitContractTransitions;

        if (blockGasLimitContractTransitions?.Any() == true)
        {
            AuRaContractGasLimitOverride gasLimitCalculator = new(
                blockGasLimitContractTransitions.Select(blockGasLimitContractTransition =>
                        new BlockGasLimitContract(
                            abiEncoder,
                            blockGasLimitContractTransition.Value,
                            blockGasLimitContractTransition.Key,
                            envFactory.Create()))
                    .ToArray<IBlockGasLimitContract>(),
                gasLimitOverrideCache,
                auraConfig.Minimum2MlnGasPerBlockWhenUsingBlockGasLimitContract,
                new TargetAdjustedGasLimitCalculator(_specProvider, blocksConfig),
                _logManager);

            return gasLimitCalculator;
        }

        // do not return target gas limit calculator here - this is used for validation to check if the override should have been used
        return null;
    }
}

public class AuRaDebugModuleFactory(IWorldStateManager worldStateManager, Func<ICodeInfoRepository> codeInfoRepositoryFunc, ILifetimeScope rootLifetimeScope) : DebugModuleFactory(worldStateManager, codeInfoRepositoryFunc, rootLifetimeScope)
{
    protected override ContainerBuilder ConfigureTracerContainer(ContainerBuilder builder)
    {
        return base.ConfigureTracerContainer(builder)
            .AddScoped<IReadOnlyTxProcessingScope, ITransactionProcessor, IWorldState>((txP, worldState) => new ReadOnlyTxProcessingScope(txP, worldState, Keccak.EmptyTreeHash))
            .AddScoped<ReadOnlyChainProcessingEnv, AuRaReadOnlyChainProcessingEnv>()
            .AddScoped<IBlockProcessor, ReadOnlyChainProcessingEnv>((env) => env.BlockProcessor);
    }
}

public interface IAuRaBlockProcessorFactory
{
    public AuRaBlockProcessor Create(
        ISpecProvider specProvider,
        IBlockValidator blockValidator,
        IRewardCalculator rewardCalculator,
        IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor,
        IWorldState stateProvider,
        IReceiptStorage receiptStorage,
        IBeaconBlockRootHandler beaconBlockRootHandler,
        ILogManager logManager,
        IBlockFinder blockTree,
        IWithdrawalProcessor withdrawalProcessor,
        IExecutionRequestsProcessor executionRequestsProcessor,
        IAuRaValidator? auRaValidator,
        ITxFilter? txFilter = null,
        AuRaContractGasLimitOverride? gasLimitOverride = null,
        ContractRewriter? contractRewriter = null,
        IBlockCachePreWarmer? preWarmer = null);
}

public class AuRaBlockProcessorFactory : IAuRaBlockProcessorFactory
{
    public AuRaBlockProcessor Create(ISpecProvider specProvider, IBlockValidator blockValidator,
        IRewardCalculator rewardCalculator, IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor, IWorldState stateProvider,
        IReceiptStorage receiptStorage, IBeaconBlockRootHandler beaconBlockRootHandler, ILogManager logManager,
        IBlockFinder blockTree, IWithdrawalProcessor withdrawalProcessor, IExecutionRequestsProcessor executionRequestsProcessor,
        IAuRaValidator? auRaValidator, ITxFilter? txFilter = null, AuRaContractGasLimitOverride? gasLimitOverride = null,
        ContractRewriter? contractRewriter = null, IBlockCachePreWarmer? preWarmer = null) =>
        new(
            specProvider,
            blockValidator,
            rewardCalculator,
            blockTransactionsExecutor,
            stateProvider,
            receiptStorage,
            beaconBlockRootHandler,
            logManager,
            blockTree,
            withdrawalProcessor,
            executionRequestsProcessor,
            auRaValidator,
            txFilter,
            gasLimitOverride,
            contractRewriter, preWarmer);
}

