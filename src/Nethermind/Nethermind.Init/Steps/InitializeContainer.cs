// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(typeof(InitializeBlockTree), typeof(InitializeStateDb))]
public class InitializeContainer: IStep
{
    private readonly INethermindApi _api;

    public InitializeContainer(INethermindApi api)
    {
        _api = api;
    }

    public Task Execute(CancellationToken cancellationToken)
    {
        ContainerBuilder builder = new ContainerBuilder();
        builder.RegisterModule(new CoreModule(_api, _api.ConfigProvider, _api.EthereumJsonSerializer, _api.LogManager));
        builder.RegisterModule(new BlockchainModule(_api));
        builder.RegisterModule(new StateModule(_api));

        foreach (INethermindPlugin nethermindPlugin in _api.Plugins)
        {
            if (!nethermindPlugin.IsActive(_api)) continue;
            if (nethermindPlugin is not IModule autofacModule) continue;

            builder.RegisterModule(autofacModule);
        }

        _api.Container = builder.Build();
        _api.DisposeStack.Push((IDisposable)_api.Container);
        return Task.CompletedTask;
    }
}

public class StateModule : Module
{
    private readonly INethermindApi _api;

    public StateModule(INethermindApi api)
    {
        _api = api;
    }

    protected override void Load(ContainerBuilder builder)
    {
        // Obviously this is still shared globally, but we can start detecting which part requires a world state as
        // without explicitly specifying a lifetime, it will crash.
        builder.Register<IWorldState>(_ => _api.WorldState!)
            .InstancePerMatchingLifetimeScope(NethermindScope.WorldState);
    }
}

public class BlockchainModule : Module
{
    private readonly INethermindApi _api;

    public BlockchainModule(INethermindApi api)
    {
        _api = api;
    }

    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<TxValidator>();

        builder.RegisterInstance(_api.BlockTree!)
            .As<IBlockTree>();
        builder.RegisterInstance(_api.BlockTree!.AsReadOnly())
            .As<IBlockFinder>();
        builder.RegisterInstance(_api.ReceiptStorage!)
            .As<IReceiptStorage>();

        builder.RegisterType<TransactionComparerProvider>()
            .As<ITransactionComparerProvider>();
        builder.RegisterType<ReceiptCanonicalityMonitor>()
            .As<IReceiptMonitor>();
        builder.RegisterType<BlockhashProvider>()
            .As<IBlockhashProvider>();
        builder.RegisterType<VirtualMachine>()
            .As<IVirtualMachine>();
        builder.RegisterType<TransactionProcessor>()
            .As<ITransactionProcessor>();
        builder.RegisterInstance(NullSealEngine.Instance)
            .As<ISealValidator>();
        builder.RegisterType<HeaderValidator>()
            .As<IHeaderValidator>();
    }
}
