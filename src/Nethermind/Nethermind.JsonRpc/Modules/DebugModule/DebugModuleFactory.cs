// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Facade;

namespace Nethermind.JsonRpc.Modules.DebugModule;

public class AutoDebugModuleFactory(IWorldStateManager worldStateManager, Func<ICodeInfoRepository> codeInfoRepositoryFunc, ILifetimeScope rootLifetimeScope): IRpcModuleFactory<IDebugRpcModule>
{
    protected virtual ContainerBuilder ConfigureTracerContainer(ContainerBuilder builder)
    {
        return builder
                .AddScoped<ChangeableTransactionProcessorAdapter>()
                .AddScoped<ITransactionProcessorAdapter, ChangeableTransactionProcessorAdapter>()
                .AddScoped(ctx => ctx.ResolveKeyed<IBlockProcessor.IBlockTransactionsExecutor>(IBlockProcessor.IBlockTransactionsExecutor.Validation))
                .AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>()
                .AddScoped<BlockchainProcessor.Options>(BlockchainProcessor.Options.NoReceipts)
                .AddScoped<IOverridableTxProcessorSource, AutoOverridableTxProcessingEnv>()
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
