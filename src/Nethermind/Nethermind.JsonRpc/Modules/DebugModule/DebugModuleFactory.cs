// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Container;
using System.Threading;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State.OverridableEnv;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.JsonRpc.Modules.DebugModule;

public class DebugModuleFactory(
    IOverridableEnvFactory envFactory,
    ILifetimeScope rootLifetimeScope,
    IBlockValidationModule[] validationBlockProcessingModules,
    IPrefixStateSeedSource prefixSeeds,
    IFlatDbConfig flatDbConfig,
    ILogManager logManager
) : IRpcModuleFactory<IDebugRpcModule>
{
    private readonly Lock _lock = new();
    private ParallelBlockTracer? _parallelTracer;

    private ContainerBuilder ConfigureTracerContainer(ContainerBuilder builder) =>
        builder
            // Standard configuration
            // Note: Not overriding `IReceiptStorage` to null.
            .AddModule(validationBlockProcessingModules)
            .AddModule(new TransactionTraceModule(validationBlockProcessingModules))
            .AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>()
            .AddScoped<BlockchainProcessor.Options>(BlockchainProcessor.Options.NoReceipts)

            // So the debug rpc change the adapter sometime.
            .AddScoped<ITransactionProcessorAdapter, ChangeableTransactionProcessorAdapter>()

            // The EIP-7928 BAL pool builds its per-worker adapters from this factory; route them through the
            // same ChangeableTransactionProcessorAdapter so they honour the tracer's runtime Execute↔Trace swap.
            .AddScoped<TransactionProcessorAdapterFactory, ChangeableTransactionProcessorAdapter>(
                static changeable => changeable.ForProcessor);

    public IDebugRpcModule Create()
    {
        IOverridableEnv env = envFactory.Create();
        IParallelBlockTracer? parallelTracer = ParallelTracer();

        ILifetimeScope tracerLifecycle = rootLifetimeScope.BeginLifetimeScope((builder) =>
        {
            ConfigureTracerContainer(builder).AddModule(env);
            if (parallelTracer is not null) builder.AddScoped<IParallelBlockTracer>(parallelTracer);
        });

        // Pass only `IGethStyleTracer` into the debug rpc lifetime.
        // This is to prevent leaking processor or world state accidentally.
        // `GethStyleTracer` must be very careful to always dispose overridable env.
        ILifetimeScope debugRpcModuleLifetime = rootLifetimeScope.BeginLifetimeScope((builder) => builder
            .AddScoped<IGethStyleTracer>(tracerLifecycle.Resolve<IGethStyleTracer>()));

        debugRpcModuleLifetime.Disposer.AddInstanceForAsyncDisposal(tracerLifecycle);
        rootLifetimeScope.Disposer.AddInstanceForAsyncDisposal(debugRpcModuleLifetime);

        return debugRpcModuleLifetime.Resolve<IDebugRpcModule>();
    }

    /// <summary>Shared by every debug module instance: the workers' environments are the expensive part, and a
    /// node-wide cap on them is the point.</summary>
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
        IOverridableEnv env = envFactory.Create();
        ILifetimeScope scope = rootLifetimeScope.BeginLifetimeScope((builder) =>
            ConfigureTracerContainer(builder)
                .AddModule(env)
                .Add<ParallelBlockTracer.Components>());
        return new ParallelBlockTracer.OwnedEnvironment(scope.Resolve<IOverridableEnv<ParallelBlockTracer.Components>>(), scope);
    }
}
