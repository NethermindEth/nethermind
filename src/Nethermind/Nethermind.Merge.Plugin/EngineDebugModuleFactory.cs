// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Modules;
using Nethermind.State.OverridableEnv;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using IDebugRpcModule = Nethermind.Merge.Plugin.IDebugRpcModule;

namespace Nethermind.Merge.Plugin;
public class DebugModuleFactory(
    IOverridableEnvFactory envFactory,
    ILifetimeScope rootLifetimeScope,
    IBlockValidationModule[] validationBlockProcessingModules
) : IRpcModuleFactory<IDebugRpcModule>
{
    private ContainerBuilder ConfigureProcessorContainer(ContainerBuilder builder) =>
        builder
            // Standard configuration
            // Note: Not overriding `IReceiptStorage` to null.
            .AddModule(validationBlockProcessingModules)
            .AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>()
            .AddScoped<BlockchainProcessor.Options>(BlockchainProcessor.Options.NoReceipts)

            // So the debug rpc change the adapter sometime.
            .AddScoped<ITransactionProcessorAdapter, ChangeableTransactionProcessorAdapter>();

    public IDebugRpcModule Create()
    {
        IOverridableEnv env = envFactory.Create();

        ILifetimeScope processorLifecyccle = rootLifetimeScope.BeginLifetimeScope((builder) =>
            ConfigureProcessorContainer(builder)
                .AddModule(env));

        // Pass only `IBlockChainProcessor` into the debug rpc lifetime.
        ILifetimeScope debugRpcModuleLifetime = rootLifetimeScope.BeginLifetimeScope((builder) => builder
            .AddScoped<IBlockchainProcessor>(processorLifecyccle.Resolve<IBlockchainProcessor>()));

        debugRpcModuleLifetime.Disposer.AddInstanceForAsyncDisposal(processorLifecyccle);
        rootLifetimeScope.Disposer.AddInstanceForAsyncDisposal(debugRpcModuleLifetime);

        return debugRpcModuleLifetime.Resolve<IDebugRpcModule>();
    }
}
