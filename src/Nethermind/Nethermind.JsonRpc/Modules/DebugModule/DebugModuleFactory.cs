// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules.DebugModule;

public class DebugModuleFactory(IWorldStateManager worldStateManager, Func<ICodeInfoRepository> codeInfoRepositoryFunc, ILifetimeScope rootLifetimeScope) : IRpcModuleFactory<IDebugRpcModule>
{
    protected virtual ContainerBuilder ConfigureTracerContainer(ContainerBuilder builder)
    {
        return builder
                // Standard configuration
                // Note: Not overriding `IReceiptStorage` to null.
                .Bind<IBlockProcessor.IBlockTransactionsExecutor, IValidationTransactionExecutor>()
                .AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>()
                .AddScoped<BlockchainProcessor.Options>(BlockchainProcessor.Options.NoReceipts)

                // So the debug rpc change the adapter sometime.
                .AddScoped<ITransactionProcessorAdapter, ChangeableTransactionProcessorAdapter>()
            ;
    }

    public IDebugRpcModule Create()
    {
        IOverridableWorldScope overridableScope = worldStateManager.CreateOverridableWorldScope();
        IOverridableCodeInfoRepository codeInfoRepository = new OverridableCodeInfoRepository(codeInfoRepositoryFunc());

        ILifetimeScope tracerLifecyccle = rootLifetimeScope.BeginLifetimeScope((builder) =>
        {
            ConfigureTracerContainer(builder)
                .AddSingleton<IWorldState>(overridableScope.WorldState)
                .AddSingleton<ICodeInfoRepository>(codeInfoRepository)

                .AddScoped<IOverridableTxProcessorSource, OverridableTxProcessingEnv>() // GethStyleTracer still need this
                .AddScoped<IOverridableWorldScope>(overridableScope)
                .AddScoped<IOverridableCodeInfoRepository>(codeInfoRepository);
        });

        // Pass only `IGethStyleTracer` into the debug rpc lifetime.
        // This is to prevent leaking processor or world state accidentally.
        // `GethStyleTracer` must be very careful to always dispose overridable env.
        ILifetimeScope debugRpcModuleLifetime = rootLifetimeScope.BeginLifetimeScope((builder) => builder
            .AddScoped<IGethStyleTracer>(tracerLifecyccle.Resolve<IGethStyleTracer>()));

        debugRpcModuleLifetime.Disposer.AddInstanceForAsyncDisposal(tracerLifecyccle);
        rootLifetimeScope.Disposer.AddInstanceForAsyncDisposal(debugRpcModuleLifetime);

        return debugRpcModuleLifetime.Resolve<IDebugRpcModule>();
    }
}
