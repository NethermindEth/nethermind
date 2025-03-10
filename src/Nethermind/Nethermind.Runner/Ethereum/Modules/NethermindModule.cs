// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Era1;
using Nethermind.Init.Steps;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Runner.Ethereum.Modules;

public class NethermindModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddModule(new BuiltInStepsModule())
            .AddModule(new StartRpcStepsModule())
            .AddModule(new EraModule())
            .AddSource(new ConfigRegistrationSource())
            .AddModule(new DbModule())
            .AddModule(new NethermindInvariantChecks())

            .AddSource(new FallbackToFieldFromApi<IApiWithNetwork>())
            .AddSource(new FallbackToFieldFromApi<IApiWithBlockchain>())
            .AddSource(new FallbackToFieldFromApi<IApiWithStores>())
            .AddSource(new FallbackToFieldFromApi<IBasicApi>())
            .Bind<IApiWithNetwork, INethermindApi>()
            .Bind<IApiWithBlockchain, INethermindApi>()
            .Bind<IApiWithStores, INethermindApi>()
            .Bind<IBasicApi, INethermindApi>()

            .AddSingleton<EthereumRunner>()
            .AddSingleton<IEthereumStepsLoader, EthereumStepsLoader>()
            .AddSingleton<EthereumStepsManager>()
            .AddSingleton<NethermindApi>()
            .AddSingleton<ISpecProvider, ChainSpecBasedSpecProvider>()
            .Bind<INethermindApi, NethermindApi>()
            ;

        /*
        builder
            .RegisterInstance(nethermindApi)
            .As<NethermindApi>()
            .As<INethermindApi>()
            // For steps that use explicit type, like TaikoNethermindApi.
            .As(nethermindApi.GetType());
            */
    }
}
