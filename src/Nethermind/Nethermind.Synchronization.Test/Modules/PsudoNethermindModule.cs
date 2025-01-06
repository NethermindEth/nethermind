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
using Nethermind.Init;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Stats;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Synchronization.Test.Modules;

/// <summary>
/// Create a reasonably complete nethermind configuration. May not work without `TestEnvironmentModule`.
/// </summary>
/// <param name="configProvider"></param>
/// <param name="spec"></param>
public class PsudoNethermindModule(IConfigProvider configProvider, ChainSpec spec) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);
        ConfigureWorldStateManager(builder);
        ConfigureNetwork(builder);

        builder
            .AddModule(new SynchronizerModule(configProvider.GetConfig<ISyncConfig>()))
            .AddModule(new DbModule())
            .AddModule(new BlocktreeModule())
            .AddModule(new BlockProcessingModule())
            .AddSource(new ConfigRegistrationSource())

            .AddSingleton<DisposableStack>()
            .AddSingleton<IEthereumEcdsa, ISpecProvider>((spec) => new EthereumEcdsa(spec.ChainId))
            .AddSingleton<ITimerFactory, TimerFactory>()

            .AddSingleton(configProvider)
            .AddSingleton<ChainSpec>(spec)
            .AddSingleton<ISpecProvider, ChainSpecBasedSpecProvider>()
            .AddSingleton<IFileSystem>(new FileSystem())
            .AddSingleton<IDbProvider>(new DbProvider())
            .AddSingleton<IProcessExitSource>(new ProcessExitSource(default))
            ;
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
