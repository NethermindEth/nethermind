// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.State.OverridableEnv;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.JsonRpc.Modules.DebugModule;

public class DebugModuleFactory(
    IProcessingEnvBuilder envBuilder,
    IOverridableEnvFactory envFactory,
    ILifetimeScope rootLifetimeScope
) : IRpcModuleFactory<IDebugRpcModule>
{
    public IDebugRpcModule Create()
    {
        IEnv tracer = envBuilder
            .WithOverridableEnv(envFactory.Create())
            // Standard configuration; not overriding `IReceiptStorage` to null.
            .WithBlockValidationConfiguration()
            .WithReplacedComponent<BlockchainProcessor.Options>(BlockchainProcessor.Options.NoReceipts)
            // So the debug rpc can change the adapter sometime.
            .WithReplacedComponent<ITransactionProcessorAdapter, ChangeableTransactionProcessorAdapter>()
            .Configure(builder => builder.AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>())
            .BuildAs<IEnv>();

        // Pass only `IGethStyleTracer` into the debug rpc lifetime to prevent leaking processor or world
        // state accidentally. `GethStyleTracer` must be very careful to always dispose overridable env.
        ILifetimeScope debugRpcModuleLifetime = rootLifetimeScope.BeginLifetimeScope((builder) => builder
            .AddScoped<IGethStyleTracer>(tracer.Tracer));

        debugRpcModuleLifetime.Disposer.AddInstanceForAsyncDisposal(tracer);
        rootLifetimeScope.Disposer.AddInstanceForAsyncDisposal(debugRpcModuleLifetime);

        return debugRpcModuleLifetime.Resolve<IDebugRpcModule>();
    }

    public interface IEnv : IAsyncDisposable
    {
        IGethStyleTracer Tracer { get; }
    }
}
