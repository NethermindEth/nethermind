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
using Nethermind.Evm.OverridableEnv;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa.InitializationSteps;

public class AuRaTraceModuleFactory(IOverridableEnvFactory envFactory, ILifetimeScope rootLifetimeScope) : TraceModuleFactory(envFactory, rootLifetimeScope)
{
    protected override ContainerBuilder ConfigureCommonBlockProcessing<T>(ContainerBuilder builder)
    {
        return base.ConfigureCommonBlockProcessing<T>(builder)
            .AddScoped<IBlockProcessor, AuRaRpcBlockProcessorFactory>((env) => env.CreateBlockProcessor());
    }
}

public class AuRaRpcBlockProcessorFactory(
    IWorldState worldState,
    ITransactionProcessor transactionProcessor,
    IBeaconBlockRootHandler beaconBlockRootHandler,
    IExecutionRequestsProcessor executionRequestsProcessor,
    IBlockValidator blockValidator,
    IRewardCalculator rewardCalculator,
    IReceiptStorage receiptStorage,
    IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor,
    IAuRaBlockProcessorFactory factory)
{
    public IBlockProcessor CreateBlockProcessor()
    {
        return factory.Create(
            blockValidator!,
            rewardCalculator,
            transactionsExecutor,
            worldState,
            receiptStorage,
            beaconBlockRootHandler,
            transactionProcessor,
            executionRequestsProcessor,
            auRaValidator: null
        );
    }
}

public class AuRaDebugModuleFactory(IOverridableEnvFactory envFactory, ILifetimeScope rootLifetimeScope) : DebugModuleFactory(envFactory, rootLifetimeScope)
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
        IBlockValidator blockValidator,
        IRewardCalculator rewardCalculator,
        IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor,
        IWorldState stateProvider,
        IReceiptStorage receiptStorage,
        IBeaconBlockRootHandler beaconBlockRootHandler,
        ITransactionProcessor transactionProcessor,
        IExecutionRequestsProcessor executionRequestsProcessor,
        IAuRaValidator? auRaValidator,
        IBlockCachePreWarmer? preWarmer = null);
}

public class AuRaBlockProcessorFactory(
    AuRaChainSpecEngineParameters parameters,
    IBlockTree blockTree,
    ISpecProvider specProvider,
    AuRaGasLimitOverrideFactory gasLimitOverrideFactory,
    TxAuRaFilterBuilders txAuRaFilterBuilders,
    ILogManager logManager
) : IAuRaBlockProcessorFactory
{
    public AuRaBlockProcessor Create(
        IBlockValidator blockValidator,
        IRewardCalculator rewardCalculator,
        IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor,
        IWorldState stateProvider,
        IReceiptStorage receiptStorage,
        IBeaconBlockRootHandler beaconBlockRootHandler,
        ITransactionProcessor transactionProcessor,
        IExecutionRequestsProcessor executionRequestsProcessor,
        IAuRaValidator? auRaValidator,
        IBlockCachePreWarmer? preWarmer = null)
    {
        IDictionary<long, IDictionary<Address, byte[]>> rewriteBytecode = parameters.RewriteBytecode;
        ContractRewriter? contractRewriter = rewriteBytecode?.Count > 0 ? new ContractRewriter(rewriteBytecode) : null;

        ITxFilter txFilter = txAuRaFilterBuilders.CreateAuRaTxFilter(new ServiceTxFilter(specProvider));

        return new AuRaBlockProcessor(
            specProvider,
            blockValidator,
            rewardCalculator,
            blockTransactionsExecutor,
            stateProvider,
            receiptStorage,
            beaconBlockRootHandler,
            logManager,
            blockTree,
            NullWithdrawalProcessor.Instance,
            executionRequestsProcessor,
            auRaValidator,
            txFilter,
            gasLimitOverrideFactory.GetGasLimitCalculator(),
            contractRewriter, preWarmer);
    }
}
