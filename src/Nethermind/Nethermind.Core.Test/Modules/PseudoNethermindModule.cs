// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Reflection;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Specs;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Init;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Module = Autofac.Module;

namespace Nethermind.Core.Test.Modules;

/// <summary>
/// Create a reasonably complete nethermind configuration.
/// It should not really have any test specific configuration which is set by `TestEnvironmentModule`.
/// May not work without `TestEnvironmentModule`.
/// </summary>
/// <param name="configProvider"></param>
/// <param name="spec"></param>
public class PseudoNethermindModule(ChainSpec spec, IConfigProvider configProvider, ILogManager logManager) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        ISyncConfig syncConfig = configProvider.GetConfig<ISyncConfig>();

        base.Load(builder);
        builder
            .AddModule(new AppInputModule(spec, configProvider, logManager))

            .AddModule(new SynchronizerModule(syncConfig))
            .AddModule(new NetworkModule())
            .AddModule(new DbModule())
            .AddModule(new WorldStateModule())
            .AddModule(new BlockTreeModule())
            .AddModule(new BlockProcessingModule())
            .AddSource(new ConfigRegistrationSource())

            // Environments
            .AddSingleton<DisposableStack>()
            .AddSingleton<ITimerFactory, TimerFactory>()
            .AddSingleton<IBackgroundTaskScheduler, MainBlockProcessingContext, IInitConfig>((blockProcessingContext, initConfig) => new BackgroundTaskScheduler(
                blockProcessingContext.BlockProcessor,
                initConfig.BackgroundTaskConcurrency,
                logManager))
            .AddSingleton<IFileSystem>(new FileSystem())
            .AddSingleton<IDbProvider>(new DbProvider())
            .AddSingleton<IProcessExitSource>(new ProcessExitSource(default))

            // Crypto
            .AddSingleton<ICryptoRandom>(new CryptoRandom())
            .AddSingleton<IEthereumEcdsa>(new EthereumEcdsa(spec.ChainId))
            .Bind<IEcdsa, IEthereumEcdsa>()
            .AddSingleton<IEciesCipher, EciesCipher>()
            ;


        // Yep... this global thing need to work.
        builder.RegisterBuildCallback((_) =>
        {
            Assembly? assembly = Assembly.GetAssembly(typeof(NetworkNodeDecoder));
            if (assembly is not null)
            {
                Rlp.RegisterDecoders(assembly, canOverrideExistingDecoders: true);
            }
        });
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
