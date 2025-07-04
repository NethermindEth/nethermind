// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.State.OverridableEnv;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Trace;

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
