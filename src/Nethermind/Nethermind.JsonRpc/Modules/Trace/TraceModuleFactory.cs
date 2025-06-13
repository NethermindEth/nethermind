// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules.Trace;

public class TraceModuleFactory(IWorldStateManager worldStateManager, Func<ICodeInfoRepository> codeInfoRepositoryFunc, ILifetimeScope rootLifetimeScope) : ModuleFactoryBase<ITraceRpcModule>
{
    protected virtual ContainerBuilder ConfigureCommonBlockProcessing<T>(ContainerBuilder builder) where T : ITransactionProcessorAdapter
    {
        return builder

                // More or less standard except for configurable `ITransactionProcessorAdapter`.
                // Note: Not overriding `IReceiptStorage` to null.
                .Bind<IBlockProcessor.IBlockTransactionsExecutor, IValidationTransactionExecutor>()
                .AddScoped<ITransactionProcessorAdapter, T>() // T can be trace or execute
                .AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>()
                .AddScoped<BlockchainProcessor.Options>(BlockchainProcessor.Options.NoReceipts)
                .AddScoped<IBlockValidator>(Always.Valid) // Why?

                .AddDecorator<IRewardCalculator, MergeRpcRewardCalculator>() // TODO: Check, what if this is pre merge?
            ;
    }

    public override ITraceRpcModule Create()
    {
        IOverridableWorldScope overridableScope = worldStateManager.CreateOverridableWorldScope();
        IOverridableCodeInfoRepository codeInfoRepository = new OverridableCodeInfoRepository(codeInfoRepositoryFunc());

        // Note: The processing block has no concern with override's and scoping. As far as its concern, a standard
        // world state and code info repository is used.
        ILifetimeScope rpcProcessingScope = rootLifetimeScope.BeginLifetimeScope((builder) =>
            ConfigureCommonBlockProcessing<TraceTransactionProcessorAdapter>(builder)
                .AddScoped<ICodeInfoRepository>(codeInfoRepository)
                .AddScoped<IWorldState>(overridableScope.WorldState));
        ILifetimeScope validationProcessingScope = rootLifetimeScope.BeginLifetimeScope((builder) =>
            ConfigureCommonBlockProcessing<ExecuteTransactionProcessorAdapter>(builder)
                .AddScoped<ICodeInfoRepository>(codeInfoRepository)
                .AddScoped<IWorldState>(overridableScope.WorldState));

        ILifetimeScope tracerLifetimeScope = rootLifetimeScope.BeginLifetimeScope((builder) =>
        {
            builder
                .AddScoped<IOverridableWorldScope>(overridableScope)
                .AddScoped<IOverridableCodeInfoRepository>(codeInfoRepository)

                .AddScoped<IWorldState>(overridableScope.WorldState)
                .AddScoped<ICodeInfoRepository>(codeInfoRepository)

                .AddScoped<IOverridableTxProcessorSource, OverridableTxProcessingEnv>()
                .AddScoped<ITracerEnv, IOverridableTxProcessorSource>((scope) => new TracerEnv(new Tracer(
                        overridableScope.WorldState,
                        rpcProcessingScope.Resolve<IBlockchainProcessor>(),
                        validationProcessingScope.Resolve<IBlockchainProcessor>(),
                        traceOptions: ProcessingOptions.TraceTransactions),
                    scope))
                ;
        });

        // Note: Only `ITracerEnv` is exposed. This is because the tracer must be run in a well defined block processing scope
        // otherwise, it risk memory leak.
        ILifetimeScope rpcLifetimeScope = rootLifetimeScope.BeginLifetimeScope((builder) =>
        {
            builder.AddScoped<ITracerEnv>(tracerLifetimeScope.Resolve<ITracerEnv>());
        });

        tracerLifetimeScope.Disposer.AddInstanceForAsyncDisposal(rpcProcessingScope);
        tracerLifetimeScope.Disposer.AddInstanceForAsyncDisposal(validationProcessingScope);
        rpcLifetimeScope.Disposer.AddInstanceForAsyncDisposal(tracerLifetimeScope);
        rootLifetimeScope.Disposer.AddInstanceForAsyncDisposal(rpcLifetimeScope);

        return rpcLifetimeScope.Resolve<ITraceRpcModule>();
    }
}
