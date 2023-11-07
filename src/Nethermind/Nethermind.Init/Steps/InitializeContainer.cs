// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;

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

        foreach (INethermindPlugin nethermindPlugin in _api.Plugins)
        {
            if (nethermindPlugin is IModule autofacModule)
            {
                builder.RegisterModule(autofacModule);
            }
        }

        _api.Container = builder.Build();
        return Task.CompletedTask;
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
        builder.RegisterInstance(_api.SpecProvider!)
            .As<ISpecProvider>();
        builder.RegisterType<TxValidator>();
    }
}
