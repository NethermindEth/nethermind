// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State.OverridableEnv;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Modules.Subscribe;

namespace Nethermind.JsonRpc.Modules.DebugModule;

public class DebugModuleFactory(
    ITraceEnvFactory envFactory,
    ILifetimeScope rootLifetimeScope,
    IBlockValidationModule[] validationBlockProcessingModules,
    IPrefixStateSeedSource prefixSeeds,
    ParallelTraceBudgets parallelBudgets,
    ILogManager logManager
) : IRpcModuleFactory<IDebugRpcModule>
{
    private readonly SubscriptionManager _traceChainSubscriptions = new(new SubscriptionFactory(), logManager);
    private readonly SharedParallelBlockTracer _parallelTracer = new(envFactory, rootLifetimeScope, prefixSeeds, parallelBudgets, logManager,
        builder => ConfigureTracerContainer(builder, validationBlockProcessingModules));

    private static ContainerBuilder ConfigureTracerContainer(ContainerBuilder builder, IBlockValidationModule[] validationBlockProcessingModules) =>
        builder
            // Standard configuration
            // Note: Not overriding `IReceiptStorage` to null.
            .AddModule(validationBlockProcessingModules)
            .AddModule(new TransactionTraceModule(validationBlockProcessingModules))
            .AddScoped<TraceChainBlockExecutor>()

            // So the debug rpc change the adapter sometime.
            .AddScoped<ITransactionProcessorAdapter, ChangeableTransactionProcessorAdapter>()

            // The EIP-7928 BAL pool builds its per-worker adapters from this factory; route them through the
            // same ChangeableTransactionProcessorAdapter so they honour the tracer's runtime Execute↔Trace swap.
            .AddScoped<TransactionProcessorAdapterFactory, ChangeableTransactionProcessorAdapter>(
                static changeable => changeable.ForProcessor);

    public IDebugRpcModule Create()
    {
        IOverridableEnv env = envFactory.CreateForTracing();
        IParallelBlockTracer? parallelTracer = _parallelTracer.Get();

        ILifetimeScope tracerLifecycle = rootLifetimeScope.BeginLifetimeScope((builder) =>
        {
            ConfigureTracerContainer(builder, validationBlockProcessingModules).AddModule(env);
            if (parallelTracer is not null) builder.AddScoped<IParallelBlockTracer>(parallelTracer);
        });

        // Pass only tracing entry points into the RPC lifetime, never a processor or world state outside its scope.
        ILifetimeScope debugRpcModuleLifetime = rootLifetimeScope.BeginLifetimeScope((builder) => builder
            .AddScoped<IGethStyleTracer>(tracerLifecycle.Resolve<IGethStyleTracer>()));

        debugRpcModuleLifetime.Disposer.AddInstanceForAsyncDisposal(tracerLifecycle);
        rootLifetimeScope.Disposer.AddInstanceForAsyncDisposal(debugRpcModuleLifetime);

        IDebugRpcModule module = debugRpcModuleLifetime.Resolve<IDebugRpcModule>();
        if (module is DebugRpcModule debugModule)
        {
            debugModule.TraceChainSubscriptions = _traceChainSubscriptions;
            debugModule.TraceChainReplay = tracerLifecycle.Resolve<TraceChainBlockExecutor>().Trace;
        }
        return module;
    }
}
