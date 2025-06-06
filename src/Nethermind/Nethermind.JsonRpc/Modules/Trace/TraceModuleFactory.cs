// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules.Trace;

public class AutoTraceModuleFactory(IWorldStateManager worldStateManager, ILifetimeScope rootLifetimeScope) : ModuleFactoryBase<ITraceRpcModule>
{

    protected virtual ContainerBuilder ConfigureCommonBlockProcessing(ContainerBuilder builder)
    {
        // Note: Not overriding `IReceiptStorage` to null.
        return builder
            .AddScoped<IRewardCalculator, IRewardCalculatorSource, ITransactionProcessor>((rewardSource, txP) => rewardSource.Get(txP)) // TODO: Check if can move globally
            .AddDecorator<IRewardCalculator, MergeRpcRewardCalculator>() // TODO: Check, what if this is pre merge?
            .AddScoped<IBlockValidator>(Always.Valid) // Why?
            .AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>()
            .AddScoped<BlockchainProcessor.Options>(BlockchainProcessor.Options.NoReceipts);
    }

    public override ITraceRpcModule Create()
    {
        IOverridableWorldScope overridableScope = worldStateManager.CreateOverridableWorldScope();

        ILifetimeScope rpcProcessingScope = rootLifetimeScope.BeginLifetimeScope((builder) => ConfigureCommonBlockProcessing(builder)
            .AddScoped<IWorldState>(overridableScope.WorldState)
            .AddScoped<IBlockProcessor.IBlockTransactionsExecutor>(ctx =>
                ctx.ResolveKeyed<IBlockProcessor.IBlockTransactionsExecutor>(IBlockProcessor.IBlockTransactionsExecutor.Rpc)
            ));
        ILifetimeScope validationProcessingScope = rootLifetimeScope.BeginLifetimeScope((builder) => ConfigureCommonBlockProcessing(builder)
            .AddScoped<IWorldState>(overridableScope.WorldState)
            .AddScoped<IBlockProcessor.IBlockTransactionsExecutor>(ctx =>
                ctx.ResolveKeyed<IBlockProcessor.IBlockTransactionsExecutor>(IBlockProcessor.IBlockTransactionsExecutor.Validation)
            ));

        ILifetimeScope traceRpcLifetimeScope = rootLifetimeScope.BeginLifetimeScope((builder) =>
        {
            builder
                .AddSingleton<IOverridableWorldScope>(overridableScope)
                .AddSingleton<IOverridableTxProcessorSource, OverridableTxProcessingEnv>()
                .AddSingleton<IReadOnlyTxProcessingScope, IOverridableTxProcessorSource>((src) => src.Build(Keccak.EmptyTreeHash))
                .AddSingleton<ITracer, IReadOnlyTxProcessingScope>((scope) => new Tracer(
                    scope,
                    rpcProcessingScope.Resolve<IBlockchainProcessor>(),
                    validationProcessingScope.Resolve<IBlockchainProcessor>(),
                    traceOptions: ProcessingOptions.TraceTransactions));
        });

        traceRpcLifetimeScope.Disposer.AddInstanceForAsyncDisposal(rpcProcessingScope);
        traceRpcLifetimeScope.Disposer.AddInstanceForAsyncDisposal(validationProcessingScope);
        rootLifetimeScope.Disposer.AddInstanceForAsyncDisposal(traceRpcLifetimeScope);

        return traceRpcLifetimeScope.Resolve<ITraceRpcModule>();
    }
}
