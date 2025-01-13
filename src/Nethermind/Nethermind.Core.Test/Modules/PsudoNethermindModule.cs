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
public class PsudoNethermindModule(IConfigProvider configProvider, ChainSpec spec) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);
        ConfigureWorldStateManager(builder);

        builder
            .AddModule(new SynchronizerModule(configProvider.GetConfig<ISyncConfig>()))
            .AddModule(new NetworkModule())
            .AddModule(new DbModule())
            .AddModule(new BlocktreeModule())
            .AddModule(new BlockProcessingModule())
            .AddSource(new ConfigRegistrationSource())

            .AddSingleton<DisposableStack>()
            .AddSingleton<IEthereumEcdsa, ISpecProvider>((spec) => new EthereumEcdsa(spec.ChainId))
            .AddSingleton<ITimerFactory, TimerFactory>()

            .AddSingleton<IBackgroundTaskScheduler>((ctx) =>
            {
                MainBlockProcessingContext blockProcessingContext = ctx.Resolve<MainBlockProcessingContext>();
                IInitConfig initConfig = ctx.Resolve<IInitConfig>();
                ILogManager logManager = ctx.Resolve<ILogManager>();

                return new BackgroundTaskScheduler(
                    blockProcessingContext.BlockProcessor,
                    initConfig.BackgroundTaskConcurrency,
                    logManager);
            })
            .AddSingleton(configProvider)
            .AddSingleton<ChainSpec>(spec)
            .AddSingleton<ISpecProvider, ChainSpecBasedSpecProvider>()
            .AddSingleton<IFileSystem>(new FileSystem())
            .AddSingleton<IDbProvider>(new DbProvider())
            .AddSingleton<IProcessExitSource>(new ProcessExitSource(default))
            .AddSingleton<ICryptoRandom>(new CryptoRandom())
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

    private void ConfigureWorldStateManager(ContainerBuilder builder)
    {
        builder
            .AddSingleton<PruningTrieStateFactory>()
            .AddSingleton<PruningTrieStateFactoryOutput>()

            .Map<PruningTrieStateFactoryOutput, IWorldStateManager>((o) => o.WorldStateManager)
            .Map<IWorldStateManager, IStateReader>((m) => m.GlobalStateReader)
            .Map<PruningTrieStateFactoryOutput, INodeStorage>((m) => m.NodeStorage);
    }

    private class PruningTrieStateFactoryOutput
    {
        public IWorldStateManager WorldStateManager { get; }
        public INodeStorage NodeStorage { get; }

        public PruningTrieStateFactoryOutput(PruningTrieStateFactory factory)
        {
            (IWorldStateManager worldStateManager, INodeStorage mainNodeStorage, CompositePruningTrigger _) = factory.Build();
            WorldStateManager = worldStateManager;
            NodeStorage = mainNodeStorage;
        }
    }
}
