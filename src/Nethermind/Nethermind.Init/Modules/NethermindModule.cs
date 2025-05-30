// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.ServiceStopper;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Era1;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Runner.Ethereum.Modules;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.TxPool;

namespace Nethermind.Init.Modules;

/// <summary>
/// Full currently on production nethermind module, excluding plugins, and fallback to INethermindApi.
/// Not able to initialize all component without INethermindApi integration and running IStep correctly.
/// For testing without having to run ISteps, see <see cref="PseudoNethermindModule"/>.
/// </summary>
/// <param name="configProvider"></param>
public class NethermindModule(ChainSpec chainSpec, IConfigProvider configProvider, ILogManager logManager) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            .AddServiceStopper()
            .AddModule(new AppInputModule(chainSpec, configProvider, logManager))
            .AddModule(new NetworkModule(configProvider))
            .AddModule(new DiscoveryModule(configProvider.GetConfig<IInitConfig>(), configProvider.GetConfig<INetworkConfig>()))
            .AddModule(new WorldStateModule())
            .AddModule(new BuiltInStepsModule())
            .AddModule(new RpcModules())
            .AddModule(new EraModule())
            .AddSource(new ConfigRegistrationSource())
            .AddModule(new DbModule())
            .AddModule(new BlockProcessingModule())
            .AddSingleton<ISpecProvider, ChainSpecBasedSpecProvider>()

            .Bind<IBlockFinder, IBlockTree>()
            .Bind<IEcdsa, IEthereumEcdsa>()

            .AddSingleton<TxValidator, ISpecProvider>((spec) => new TxValidator(spec.ChainId))
            .Bind<ITxValidator, TxValidator>()
            .AddSingleton<IBlockValidator, BlockValidator>()
            .AddSingleton<IHeaderValidator, HeaderValidator>()
            .AddSingleton<IUnclesValidator, UnclesValidator>()

            .AddKeyedSingleton<IProtectedPrivateKey>(IProtectedPrivateKey.NodeKey, (ctx) => ctx.Resolve<INethermindApi>().NodeKey!)
            .AddSingleton<IEciesCipher, EciesCipher>()
            .AddSingleton<ICryptoRandom, CryptoRandom>()
            .Add<IDisposableStack, AutofacDisposableStack>() // Not a singleton so that dispose is registered to correct lifetime
            ;
    }

    // Just a wrapper to make it clear, these three are expected to be available at the time of configurations.
    private class AppInputModule(ChainSpec chainSpec, IConfigProvider configProvider, ILogManager logManager) : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            base.Load(builder);

            builder
                .AddSingleton(configProvider)
                .AddSingleton<ChainSpec>(chainSpec)
                .AddSingleton<ILogManager>(logManager)
                .AddSingleton<ISpecProvider, ChainSpecBasedSpecProvider>()
                ;
        }
    }
}
