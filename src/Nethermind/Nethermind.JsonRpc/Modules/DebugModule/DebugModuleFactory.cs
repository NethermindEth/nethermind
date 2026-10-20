// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Autofac;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Facade;
using Nethermind.Int256;
using Nethermind.State.OverridableEnv;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.JsonRpc.Modules.DebugModule;

/// <param name="readOnlyQueryModules">The <see cref="ReadOnlyQueryKind.Trace"/> modules among them configure the
/// environments of every module instance and of the parallel block tracer, loaded after everything else those
/// environments register.</param>
[method: Experimental("NMEVM0001")]
public class DebugModuleFactory(
    ITraceEnvFactory envFactory,
    ILifetimeScope rootLifetimeScope,
    IBlockValidationModule[] validationBlockProcessingModules,
    IPrefixStateSeedSource prefixSeeds,
    ParallelTraceBudgets parallelBudgets,
    ILogManager logManager,
    IReadOnlyQueryModule[] readOnlyQueryModules
) : IRpcModuleFactory<IDebugRpcModule>
{
    /// <summary>A factory whose environments load no read-only query modules.</summary>
    public DebugModuleFactory(
        ITraceEnvFactory envFactory,
        ILifetimeScope rootLifetimeScope,
        IBlockValidationModule[] validationBlockProcessingModules,
        IPrefixStateSeedSource prefixSeeds,
        ParallelTraceBudgets parallelBudgets,
        ILogManager logManager)
        : this(envFactory, rootLifetimeScope, validationBlockProcessingModules, prefixSeeds, parallelBudgets, logManager, [])
    {
    }

    private readonly SharedParallelBlockTracer _parallelTracer = new(envFactory, rootLifetimeScope, prefixSeeds, parallelBudgets, logManager,
        builder => ConfigureTracerContainer(builder, validationBlockProcessingModules),
        builder => builder.AddReadOnlyQueryModules(readOnlyQueryModules, ReadOnlyQueryKind.Trace));

    private static ContainerBuilder ConfigureTracerContainer(ContainerBuilder builder, IBlockValidationModule[] validationBlockProcessingModules) =>
        builder
            // Standard configuration
            // Note: Not overriding `IReceiptStorage` to null.
            .AddModule(validationBlockProcessingModules)
            .AddModule(new TransactionTraceModule(validationBlockProcessingModules))
            .AddScoped<GethStyleTracer.TraceCallRequestState>()
            .AddScoped<IBlobBaseFeeOverrideProvider, TraceCallBlobBaseFeeOverrideProvider>()
            .AddDecorator<ITransactionProcessor.IBlobBaseFeeCalculator, BlobBaseFeeOverrideCalculatorDecorator>()

            // So the debug rpc change the adapter sometime.
            .AddScoped<ITransactionProcessorAdapter, ChangeableTransactionProcessorAdapter>()

            // The EIP-7928 BAL pool builds its per-worker adapters from this factory; route them through the
            // same ChangeableTransactionProcessorAdapter so they honour the tracer's runtime Execute↔Trace swap.
            .AddScoped<TransactionProcessorAdapterFactory, ChangeableTransactionProcessorAdapter>(
                static changeable => changeable.ForProcessor);

    private sealed class TraceCallBlobBaseFeeOverrideProvider(GethStyleTracer.TraceCallRequestState state) : IBlobBaseFeeOverrideProvider
    {
        /// <inheritdoc/>
        public UInt256? BlobBaseFeeOverride => state.BlobBaseFee;
    }

    public IDebugRpcModule Create()
    {
        IOverridableEnv env = envFactory.CreateForTracing();
        IParallelBlockTracer? parallelTracer = _parallelTracer.Get();

        ILifetimeScope tracerLifecycle = rootLifetimeScope.BeginLifetimeScope((builder) =>
        {
            ConfigureTracerContainer(builder, validationBlockProcessingModules).AddModule(env);
            if (parallelTracer is not null) builder.AddScoped<IParallelBlockTracer>(parallelTracer);
            // The debug_* replays and traced calls run here. Last, as in every query environment, so what a module
            // registers takes precedence.
            builder.AddReadOnlyQueryModules(readOnlyQueryModules, ReadOnlyQueryKind.Trace);
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
}
