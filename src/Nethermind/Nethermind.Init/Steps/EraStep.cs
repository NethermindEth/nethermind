// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Era1;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(typeof(InitializeBlockchain), typeof(LoadGenesisBlock))]
public class EraStep : IStep
{
    protected readonly INethermindApi _api;

    public EraStep(INethermindApi api)
    {
        _api = api;
    }

    public async Task Execute(CancellationToken cancellationToken)
    {
        IContainer container = _api.ConfigureContainerBuilderFromApiWithBlockchain(new ContainerBuilder())
            .AddModule(new EraModule())
            .Build();

        _api.DisposeStack.Push((IAsyncDisposable)container);
        _api.AdminEraService = container.Resolve<IAdminEraService>();

        await container.Resolve<EraCliRunner>().Run(cancellationToken);
    }
}
