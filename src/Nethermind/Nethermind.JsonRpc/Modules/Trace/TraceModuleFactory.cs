// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    private ContainerBuilder ConfigureCommonBlockProcessing<T>(ContainerBuilder builder) where T : ITransactionProcessorAdapter =>
        builder
            .AddModule(validationBlockProcessingModules)

            // More or less standard except for configurable `ITransactionProcessorAdapter`.
            // Note: Not overriding `IReceiptStorage` to null.
            .AddScoped<ITransactionProcessorAdapter, T>() // T can be trace or execute
            .AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>()
            .AddScoped<BlockchainProcessor.Options>(BlockchainProcessor.Options.NoReceipts)
            .AddScoped<IBlockValidator>(Always.Valid) // Why?

            .AddDecorator<IRewardCalculator, MergeRpcRewardCalculator>(); // TODO: Check, what if this is pre merge?

    public override ITraceRpcModule Create()
    {
        IOverridableEnv env = overridableEnvFactory.Create();

        // Note: The processing block has no concern with override's and scoping. As far as its concern, a standard
        // world state and code info repository is used.
        ILifetimeScope rpcProcessingScope = rootLifetimeScope.BeginLifetimeScope((builder) =>
            ConfigureCommonBlockProcessing<TraceTransactionProcessorAdapter>(builder)
                .AddModule(env));
        ILifetimeScope validationProcessingScope = rootLifetimeScope.BeginLifetimeScope((builder) =>
            ConfigureCommonBlockProcessing<ExecuteTransactionProcessorAdapter>(builder)
                .AddModule(env));

        ILifetimeScope tracerLifetimeScope = rootLifetimeScope.BeginLifetimeScope((builder) => builder
            .AddModule(env)
            .AddScoped<ITracer, IStateReader>((stateReader) => new Tracer(
                stateReader,
                rpcProcessingScope.Resolve<IBlockchainProcessor>(),
                validationProcessingScope.Resolve<IBlockchainProcessor>(),
                traceOptions: ProcessingOptions.TraceTransactions)));

        // Split out only the env to prevent accidental leak
        IOverridableEnv<ITracer> tracerEnv = tracerLifetimeScope.Resolve<IOverridableEnv<ITracer>>();

        ILifetimeScope rpcLifetimeScope = rootLifetimeScope.BeginLifetimeScope((builder) => builder
            .AddScoped(tracerEnv));

        tracerLifetimeScope.Disposer.AddInstanceForAsyncDisposal(rpcProcessingScope);
        tracerLifetimeScope.Disposer.AddInstanceForAsyncDisposal(validationProcessingScope);
        rpcLifetimeScope.Disposer.AddInstanceForAsyncDisposal(tracerLifetimeScope);
        rootLifetimeScope.Disposer.AddInstanceForAsyncDisposal(rpcLifetimeScope);

        return rpcLifetimeScope.Resolve<ITraceRpcModule>();
    }
}
