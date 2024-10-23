// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Era1;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(typeof(InitializeBlockchain))]
public class ImportEraStep : IStep
{
    protected readonly INethermindApi _api;

    public ImportEraStep(INethermindApi api)
    {
        _api = api;
    }

    public async Task Execute(CancellationToken cancellationToken)
    {
        await using IContainer container = _api.ConfigureContainerBuilderFromApiWithBlockchain(new ContainerBuilder())
            .AddModule(new EraModule())
            .AddPropertiesFrom(_api)
            .Build();
        await container.Resolve<EraCliRunner>().Run(cancellationToken);
    }

}
