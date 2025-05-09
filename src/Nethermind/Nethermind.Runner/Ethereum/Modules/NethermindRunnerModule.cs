// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Init.Modules;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Runner.Ethereum.Modules;

/// <summary>
/// Configure the whole application including plugins and its integration.
/// </summary>
/// <param name="jsonSerializer"></param>
/// <param name="chainSpec"></param>
/// <param name="configProvider"></param>
/// <param name="processExitSource"></param>
/// <param name="plugins"></param>
/// <param name="logManager"></param>
public class NethermindRunnerModule(
    IJsonSerializer jsonSerializer,
    ChainSpec chainSpec,
    IConfigProvider configProvider,
    IProcessExitSource processExitSource,
    IEnumerable<INethermindPlugin> plugins,
    ILogManager logManager
) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        IEnumerable<IConsensusPlugin> consensusPlugins = plugins.OfType<IConsensusPlugin>();
        if (consensusPlugins.Count() != 1)
        {
            throw new NotSupportedException($"Thse should be exactly one consensus plugin are enabled. Seal engine type: {chainSpec.SealEngineType}. {string.Join(", ", consensusPlugins.Select(x => x.Name))}");
        }

        IConsensusPlugin consensusPlugin = consensusPlugins.FirstOrDefault();

        builder
            .AddModule(new NethermindModule(chainSpec, configProvider, logManager))

            .AddSource(new FallbackToFieldFromApi<IApiWithNetwork>())
            .AddSource(new FallbackToFieldFromApi<IApiWithBlockchain>())
            .AddSource(new FallbackToFieldFromApi<IApiWithStores>())
            .AddSource(new FallbackToFieldFromApi<IBasicApi>())
            .Bind<IApiWithNetwork, INethermindApi>()
            .Bind<IApiWithBlockchain, INethermindApi>()
            .Bind<IApiWithStores, INethermindApi>()
            .Bind<IBasicApi, INethermindApi>()

            .AddModule(new StartRpcStepsModule())
            .AddModule(new NethermindInvariantChecks())

            .AddSingleton<EthereumRunner>()
            .AddSingleton<IEthereumStepsLoader, EthereumStepsLoader>()
            .AddSingleton<EthereumStepsManager>()
            .AddSingleton<IProcessExitSource>(processExitSource)

            .AddSingleton<NethermindApi>()
            .AddSingleton<NethermindApi.Dependencies>()
            .Bind<INethermindApi, NethermindApi>()

            .AddSingleton(jsonSerializer)
            .AddSingleton<IConsensusPlugin>(consensusPlugin)
            ;

        foreach (var plugin in plugins)
        {
            foreach (var stepInfo in plugin.GetSteps())
            {
                builder.AddStep(stepInfo);
            }

            if (plugin.Module is not null)
            {
                builder.AddModule(plugin.Module);
            }
            builder.AddSingleton<INethermindPlugin>(plugin);
        }

    }
}
