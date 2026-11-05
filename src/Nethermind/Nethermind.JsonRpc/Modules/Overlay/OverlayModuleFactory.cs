// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Autofac;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.State.OverridableEnv;

namespace Nethermind.JsonRpc.Modules.Overlay;

/// <param name="readOnlyQueryModules">The <see cref="ReadOnlyQueryKind.Trace"/> modules among them configure the
/// environment that re-executes blocks.</param>
[method: Experimental("NMEVM0001")]
public class OverlayModuleFactory(
    ILifetimeScope rootLifetimeScope,
    IOverridableEnvFactory overridableEnvFactory,
    IReadOnlyList<IBlockValidationModule> validationBlockProcessingModules,
    IReadOnlyQueryModule[] readOnlyQueryModules
) : ModuleFactoryBase<IOverlayRpcModule>
{
    /// <summary>A factory whose environment loads no read-only query modules.</summary>
    public OverlayModuleFactory(
        ILifetimeScope rootLifetimeScope,
        IOverridableEnvFactory overridableEnvFactory,
        IReadOnlyList<IBlockValidationModule> validationBlockProcessingModules)
        : this(rootLifetimeScope, overridableEnvFactory, validationBlockProcessingModules, [])
    {
    }

    public override IOverlayRpcModule Create()
    {
        IOverridableEnv overridableEnv = overridableEnvFactory.Create();

        // Transactions from the chain are charged as they were; the constructor run is unpriced, as in trace_call.
        ILifetimeScope tracerScope = rootLifetimeScope.BeginLifetimeScope((builder) => builder
            .AddModule(overridableEnv)
            .AddModule(validationBlockProcessingModules)
            .AddScoped<TransactionProcessorAdapterFactory>(static p => new UnpricedCallTraceAdapter(p))
            .AddDecorator<ITransactionProcessor.IBlobBaseFeeCalculator, UnpricedBlobFeeCalculator>()
            .AddScoped<IBlockValidator>(Always.Valid)
            .AddScoped<ITracer, Tracer>()
            .AddReadOnlyQueryModules(readOnlyQueryModules, ReadOnlyQueryKind.Trace));

        ILifetimeScope overlayRpcScope = rootLifetimeScope.BeginLifetimeScope((builder) => builder
            .AddSingleton<IOverridableEnv<ITracer>>(tracerScope.Resolve<IOverridableEnv<ITracer>>()));

        overlayRpcScope.Disposer.AddInstanceForAsyncDisposal(tracerScope);
        rootLifetimeScope.Disposer.AddInstanceForDisposal(overlayRpcScope);

        return overlayRpcScope.Resolve<IOverlayRpcModule>();
    }
}
