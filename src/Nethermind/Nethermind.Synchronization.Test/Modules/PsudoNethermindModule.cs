// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Autofac;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Init;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Stats;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;

namespace Nethermind.Synchronization.Test.Modules;

internal class PsudoNethermindModule(IConfigProvider configProvider) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddModule(new SynchronizerModule(configProvider.GetConfig<ISyncConfig>()))
            .AddModule(new DbModule())
            .AddModule(new BlocktreeModule())
            .AddModule(new BlockProcessingModule())

            .AddSource(new ConfigRegistrationSource())

            .AddSingleton(configProvider)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IDbProvider>(TestMemDbProvider.Init())

            // TODO: These two have the same responsibility?
            .AddSingleton<ChainSpec>(new ChainSpec())
            .AddSingleton<ISpecProvider>(MainnetSpecProvider.Instance)

            .AddSingleton<IFileStoreFactory>(new InMemoryDictionaryFileStoreFactory())
            .AddSingleton<IFileSystem>(Substitute.For<IFileSystem>())

            .AddSingleton<IProcessExitSource>(new ProcessExitSource(default))
            .AddSingleton<DisposableStack>()
            .AddSingleton<IEthereumEcdsa, ISpecProvider>((spec) => new EthereumEcdsa(spec.ChainId))
            .AddSingleton<ITimerFactory, TimerFactory>();

        ConfigureWorldStateManager(builder);
        ConfigureNetwork(builder);
    }

    private void ConfigureWorldStateManager(ContainerBuilder builder)
    {
        builder
            .AddSingleton<PruningTrieStateFactory>()
            .AddSingleton<PruningTrieStateFactoryOutput>()

            .Map<PruningTrieStateFactoryOutput, IWorldStateManager>((o) => o.WorldStateManager)
            .Map<IWorldStateManager, IStateReader>((m) => m.GlobalStateReader);
    }

    private class PruningTrieStateFactoryOutput
    {
        public IWorldStateManager WorldStateManager { get; }

        public PruningTrieStateFactoryOutput(PruningTrieStateFactory factory)
        {
            (IWorldStateManager worldStateManager, INodeStorage mainNodeStorage, CompositePruningTrigger _) = factory.Build();
            WorldStateManager = worldStateManager;
        }
    }


    private void ConfigureNetwork(ContainerBuilder builder)
    {
        builder
            .AddSingleton<IPivot, Pivot>()
            .AddSingleton<IFullStateFinder, FullStateFinder>()
            .AddSingleton<INodeStatsManager, NodeStatsManager>()
            .AddSingleton<IBeaconSyncStrategy>(No.BeaconSync)
            .AddSingleton<IBetterPeerStrategy, TotalDifficultyBetterPeerStrategy>();
    }

}
