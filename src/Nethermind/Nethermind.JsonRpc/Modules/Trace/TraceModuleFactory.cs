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
using System.Threading;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State.OverridableEnv;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules.Trace;

public class TraceModuleFactory(
    IOverridableEnvFactory overridableEnvFactory,
    ILifetimeScope rootLifetimeScope,
    IReadOnlyList<IBlockValidationModule> validationBlockProcessingModules,
    IPrefixStateSeedSource prefixSeeds,
    IFlatDbConfig flatDbConfig,
    ILogManager logManager
) : ModuleFactoryBase<ITraceRpcModule>
{
    private readonly Lock _lock = new();
    private ParallelBlockTracer? _parallelTracer;

    private ContainerBuilder ConfigureCommonBlockProcessing(ContainerBuilder builder, TransactionProcessorAdapterFactory adapterFactory) =>
        builder
            .AddModule(validationBlockProcessingModules)
            .AddModule(new TransactionTraceModule(validationBlockProcessingModules))

            .AddScoped<TransactionProcessorAdapterFactory>(adapterFactory)
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
            ConfigureCommonBlockProcessing(builder, static p => new TraceTransactionProcessorAdapter(p))
                .AddModule(env));
        ILifetimeScope validationProcessingScope = rootLifetimeScope.BeginLifetimeScope((builder) =>
            ConfigureCommonBlockProcessing(builder, static p => new ExecuteTransactionProcessorAdapter(p))
                .AddModule(env));

        ILifetimeScope tracerLifetimeScope = rootLifetimeScope.BeginLifetimeScope((builder) => builder
            .AddModule(env)
            .AddScoped<ITracer, IStateReader>((stateReader) => new Tracer(
                stateReader,
                rpcProcessingScope.Resolve<BlockchainProcessorFacade>(),
                validationProcessingScope.Resolve<BlockchainProcessorFacade>())));

        // Split out only the env to prevent accidental leak
        IOverridableEnv<ITracer> tracerEnv = tracerLifetimeScope.Resolve<IOverridableEnv<ITracer>>();

        IParallelBlockTracer? parallelTracer = ParallelTracer();
        ILifetimeScope rpcLifetimeScope = rootLifetimeScope.BeginLifetimeScope((builder) =>
        {
            builder.AddScoped(tracerEnv);
            if (parallelTracer is not null) builder.AddScoped<IParallelBlockTracer>(parallelTracer);
        });

        tracerLifetimeScope.Disposer.AddInstanceForAsyncDisposal(rpcProcessingScope);
        tracerLifetimeScope.Disposer.AddInstanceForAsyncDisposal(validationProcessingScope);
        rpcLifetimeScope.Disposer.AddInstanceForAsyncDisposal(tracerLifetimeScope);
        rootLifetimeScope.Disposer.AddInstanceForAsyncDisposal(rpcLifetimeScope);

        return rpcLifetimeScope.Resolve<ITraceRpcModule>();
    }

    /// <summary>Shared by every trace module instance; its environments execute, as the module's own replay does,
    /// so a parallel trace charges the same gas the sequential one would.</summary>
    private ParallelBlockTracer? ParallelTracer()
    {
        if (!prefixSeeds.Enabled) return null;

        lock (_lock)
        {
            if (_parallelTracer is null)
            {
                _parallelTracer = new ParallelBlockTracer(BuildParallelEnvironment, prefixSeeds, ParallelBlockTracer.DegreeFrom(flatDbConfig), logManager);
                rootLifetimeScope.Disposer.AddInstanceForDisposal(_parallelTracer);
            }

            return _parallelTracer;
        }
    }

    private IOverridableEnv<ParallelBlockTracer.Components> BuildParallelEnvironment()
    {
        IOverridableEnv env = overridableEnvFactory.Create();
        ILifetimeScope scope = rootLifetimeScope.BeginLifetimeScope((builder) =>
            ConfigureCommonBlockProcessing(builder, static p => new ExecuteTransactionProcessorAdapter(p))
                .AddModule(env)
                .Add<ParallelBlockTracer.Components>());
        return new ParallelBlockTracer.OwnedEnvironment(scope.Resolve<IOverridableEnv<ParallelBlockTracer.Components>>(), scope);
    }
}
