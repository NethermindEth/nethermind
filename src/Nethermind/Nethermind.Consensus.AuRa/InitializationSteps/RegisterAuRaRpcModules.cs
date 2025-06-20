// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa.InitializationSteps;

public class AuRaTraceModuleFactory(IWorldStateManager worldStateManager, Func<ICodeInfoRepository> codeInfoRepositoryFunc, ILifetimeScope rootLifetimeScope) : TraceModuleFactory(worldStateManager, codeInfoRepositoryFunc, rootLifetimeScope)
{
    protected override ContainerBuilder ConfigureCommonBlockProcessing<T>(ContainerBuilder builder)
    {
        return base.ConfigureCommonBlockProcessing<T>(builder)
            .AddScoped<IBlockProcessor, AuRaRpcBlockProcessorFactory>((env) => env.CreateBlockProcessor());
    }
}

public class AuRaRpcBlockProcessorFactory(
    IWorldState worldState,
    IBeaconBlockRootHandler beaconBlockRootHandler,
    IExecutionRequestsProcessor executionRequestsProcessor,
    AuRaChainSpecEngineParameters parameters,
    IBlockValidator blockValidator,
    IRewardCalculator rewardCalculator,
    IReceiptStorage receiptStorage,
    ISpecProvider specProvider,
    IBlockTree blockTree,
    ILogManager logManager,
    IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor,
    AuRaGasLimitOverrideFactory gasLimitOverrideFactory,
    IAuRaBlockProcessorFactory factory)
{
    public IBlockProcessor CreateBlockProcessor()
    {
        ITxFilter auRaTxFilter = new ServiceTxFilter(specProvider);

        IDictionary<long, IDictionary<Address, byte[]>> rewriteBytecode = parameters.RewriteBytecode;
        ContractRewriter? contractRewriter = rewriteBytecode?.Count > 0 ? new ContractRewriter(rewriteBytecode) : null;

        return factory.Create(
            specProvider!,
            blockValidator!,
            rewardCalculator,
            transactionsExecutor,
            worldState,
            receiptStorage,
            beaconBlockRootHandler,
            logManager,
            blockTree,
            NullWithdrawalProcessor.Instance,
            executionRequestsProcessor,
            auRaValidator: null,
            auRaTxFilter,
            gasLimitOverrideFactory.GetGasLimitCalculator(),
            contractRewriter
        );
    }
}

public class AuRaDebugModuleFactory(IWorldStateManager worldStateManager, Func<ICodeInfoRepository> codeInfoRepositoryFunc, ILifetimeScope rootLifetimeScope) : DebugModuleFactory(worldStateManager, codeInfoRepositoryFunc, rootLifetimeScope)
{
    protected override ContainerBuilder ConfigureTracerContainer(ContainerBuilder builder)
    {
        return base.ConfigureTracerContainer(builder)
            .AddScoped<IBlockProcessor, AuRaRpcBlockProcessorFactory>((env) => env.CreateBlockProcessor());
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

