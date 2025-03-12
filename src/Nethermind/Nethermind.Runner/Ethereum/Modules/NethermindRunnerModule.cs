// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Init.Modules;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Runner.Ethereum.Modules;

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
        base.Load(builder);

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

            .AddSingleton<NethermindApi>()
            .Bind<INethermindApi, NethermindApi>()

            .AddSingleton(jsonSerializer)

            .OnBuild((ctx) =>
            {
                INethermindApi api = ctx.Resolve<INethermindApi>();

                // TODO: These should be injected from constructor
                ISpecProvider specProvider = new ChainSpecBasedSpecProvider(chainSpec, logManager);
                FollowOtherMiners gasLimitCalculator = new FollowOtherMiners(specProvider);
                api.SpecProvider = specProvider;
                api.GasLimitCalculator = gasLimitCalculator;
                api.ProcessExit = processExitSource;
                ((List<INethermindPlugin>)api.Plugins).AddRange(plugins);

                // I would like to thank object inheritance for inspiring this laziness.
                api.Context = ctx;
            })
            ;
    }
}
