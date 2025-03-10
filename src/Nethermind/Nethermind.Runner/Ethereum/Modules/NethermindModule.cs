// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Era1;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Runner.Ethereum.Modules;

public class NethermindModule(
    ISpecProvider specProvider,
    ChainSpec chainSpec,
    INethermindApi nethermindApi,
    IProcessExitSource processExitSource,
    IConfigProvider configProvider,
    IJsonSerializer jsonSerializer,
    IGasLimitCalculator gasLimitCalculator,
    ILogManager logManager
): Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddModule(new BuiltInStepsModule())
            .AddModule(new StartRpcModule())
            .AddModule(new EraModule())
            .AddSource(new ConfigRegistrationSource())
            .AddModule(new DbModule())

            .AddSingleton<EthereumRunner>()
            .AddSingleton<IEthereumStepsLoader, EthereumStepsLoader>()
            .AddSingleton<EthereumStepsManager>()
            .AddSingleton(configProvider)
            .AddSingleton(jsonSerializer)
            .AddSingleton(logManager)
            .AddSingleton(processExitSource)
            .AddSingleton(specProvider)
            .AddSingleton(gasLimitCalculator)
            .AddSingleton(chainSpec)
            .AddSingleton(nethermindApi);
    }
}
