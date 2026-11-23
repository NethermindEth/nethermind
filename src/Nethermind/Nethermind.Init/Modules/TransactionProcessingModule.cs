// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Init.Modules;

/// <summary>Registers the Ethereum transaction execution environment shared by block processing and calls.</summary>
public class TransactionProcessingModule(int initialAccountCapacity = WorldState.DefaultInitialAccountCapacity) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<WorldState>()
            .WithParameter(nameof(initialAccountCapacity), initialAccountCapacity)
            .WithAttributeFiltering()
            .InstancePerLifetimeScope();

        builder
            .AddScoped<ITransactionProcessor.IBlobBaseFeeCalculator, BlobBaseFeeCalculator>()
            .AddScoped<EthereumTransactionProcessor>()
            .AddSingleton<ITransactionProcessorFactory, TransactionProcessorFactory<EthereumGasPolicy>>()
            .AddScoped<ICodeInfoRepository, CacheCodeInfoRepository>()
                .AddSingleton<IPrecompileProvider, EthereumPrecompileProvider>()
                .AddSingleton<ICodeCache>(StaticCodeCache.Instance)
            .BindScoped<IWorldState, WorldState>()
            .AddScoped<IVirtualMachine, EthereumVirtualMachine>()
            .AddScoped<IBlockhashProvider, BlockhashProvider>()
            .AddSingleton<IUnresolvedBlockhashPolicy>(ThrowingUnresolvedBlockhashPolicy.Instance)
            .AddSingleton<IBlockhashCache, BlockhashCache>();

        // Marked for the pre-warm handoff, which mirrors this processor's execution. A chain that registers its own
        // processor replaces the registration, and with it the mark.
        builder.Register(static ctx => ctx.Resolve<EthereumTransactionProcessor>())
            .As<ITransactionProcessor>()
            .InstancePerLifetimeScope()
            .ExternallyOwned()
            .WithMetadata(PrewarmerEnvFactory.HandoffMetadata, true);
    }
}
