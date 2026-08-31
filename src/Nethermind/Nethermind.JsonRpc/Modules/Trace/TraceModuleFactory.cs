// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.State.OverridableEnv;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules.Trace;

public class TraceModuleFactory(
    IOverridableEnvFactory overridableEnvFactory,
    ILifetimeScope rootLifetimeScope,
    IReadOnlyList<IBlockValidationModule> validationBlockProcessingModules
) : ModuleFactoryBase<ITraceRpcModule>
{
    private ContainerBuilder ConfigureCommonBlockProcessing(ContainerBuilder builder, TransactionProcessorAdapterFactory adapterFactory) =>
        builder
            .AddModule(validationBlockProcessingModules)

            .AddScoped<TransactionProcessorAdapterFactory>(adapterFactory)
            .AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>()
            .AddScoped<BlockchainProcessor.Options>(BlockchainProcessor.Options.NoReceipts)
            .AddScoped<IBlockValidator>(Always.Valid) // Why?

            .AddDecorator<IRewardCalculator, MergeRpcRewardCalculator>(); // TODO: Check, what if this is pre merge?

    public override ITraceRpcModule Create()
    {
        IOverridableEnv? env = null;
        ILifetimeScope? rpcProcessingScope = null;
        ILifetimeScope? validationProcessingScope = null;
        ILifetimeScope? tracerLifetimeScope = null;
        ILifetimeScope? rpcLifetimeScope = null;
        try
        {
            env = overridableEnvFactory.Create();

            // Note: The processing block has no concern with override's and scoping. As far as its concern, a standard
            // world state and code info repository is used.
            rpcProcessingScope = rootLifetimeScope.BeginLifetimeScope((builder) =>
                ConfigureCommonBlockProcessing(builder, static p => new TraceTransactionProcessorAdapter(p))
                    .AddModule(env));
            validationProcessingScope = rootLifetimeScope.BeginLifetimeScope((builder) =>
                ConfigureCommonBlockProcessing(builder, static p => new ExecuteTransactionProcessorAdapter(p))
                    .AddModule(env));

            tracerLifetimeScope = rootLifetimeScope.BeginLifetimeScope((builder) => builder
                .AddModule(env)
                .AddScoped<ITracer, IStateReader>((stateReader) => new Tracer(
                    stateReader,
                    rpcProcessingScope.Resolve<BlockchainProcessorFacade>(),
                    validationProcessingScope.Resolve<BlockchainProcessorFacade>(),
                    traceOptions: ProcessingOptions.TraceTransactions)));

            // Split out only the env to prevent accidental leak
            IOverridableEnv<ITracer> tracerEnv = tracerLifetimeScope.Resolve<IOverridableEnv<ITracer>>();

            rpcLifetimeScope = rootLifetimeScope.BeginLifetimeScope((builder) => builder
                .AddScoped(tracerEnv));

            ITraceRpcModule module = rpcLifetimeScope.Resolve<ITraceRpcModule>();
            tracerLifetimeScope.Disposer.AddInstanceForAsyncDisposal(rpcProcessingScope);
            rpcProcessingScope = null;
            tracerLifetimeScope.Disposer.AddInstanceForAsyncDisposal(validationProcessingScope);
            validationProcessingScope = null;
            rpcLifetimeScope.Disposer.AddInstanceForAsyncDisposal(tracerLifetimeScope);
            tracerLifetimeScope = null;
            rootLifetimeScope.Disposer.AddInstanceForAsyncDisposal(rpcLifetimeScope);
            rpcLifetimeScope = null;

            return module;
        }
        catch
        {
            rpcLifetimeScope?.Dispose();
            tracerLifetimeScope?.Dispose();
            validationProcessingScope?.Dispose();
            rpcProcessingScope?.Dispose();
            (env as IDisposable)?.Dispose();
            throw;
        }
    }
}
