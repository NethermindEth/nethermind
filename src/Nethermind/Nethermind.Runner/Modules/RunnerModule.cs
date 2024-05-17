// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Init.Steps;
using Nethermind.Runner.Ethereum;

namespace Nethermind.Runner.Modules;

public class RunnerModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder.RegisterType<EthereumRunner>();
        builder.RegisterType<EthereumStepsManager>();
        builder.RegisterType<EthereumStepsLoader>()
            .As<IEthereumStepsLoader>();
    }
}
