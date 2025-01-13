// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Reflection;
using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Api;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Init;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.State.SnapServer;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;
using Block = Nethermind.EthStats.Messages.Models.Block;
using Module = Autofac.Module;

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
                BlockProcessingModule.MainBlockProcessingContext blockProcessingContext = ctx.Resolve<BlockProcessingModule.MainBlockProcessingContext>();
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

public class NetworkModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<IBetterPeerStrategy, TotalDifficultyBetterPeerStrategy>()
            .AddSingleton<IPivot, Pivot>()
            .AddSingleton<IFullStateFinder, FullStateFinder>()
            .AddSingleton<INodeStatsManager, NodeStatsManager>()
            .AddSingleton<IBeaconSyncStrategy>(No.BeaconSync)

            .AddSingleton<IDisconnectsAnalyzer, MetricsDisconnectsAnalyzer>()
            .AddSingleton<ISessionMonitor, SessionMonitor>()
            .AddSingleton<IRlpxHost, RlpxHost>()
            .AddSingleton<IHandshakeService, HandshakeService>()
            .AddSingleton<IEciesCipher, EciesCipher>()

            .AddSingleton<IEthereumEcdsa, ISpecProvider>(specProvider => new EthereumEcdsa(specProvider.ChainId))
            .Bind<IEthereumEcdsa, IEcdsa>()

            .AddSingleton<IMessageSerializationService, ICryptoRandom, ISpecProvider>((cryptoRandom, specProvider) =>
            {
                var serializationService = new MessageSerializationService();

                Eip8MessagePad eip8Pad = new(cryptoRandom);
                serializationService.Register(new AuthEip8MessageSerializer(eip8Pad));
                serializationService.Register(new AckEip8MessageSerializer(eip8Pad));
                serializationService.Register(System.Reflection.Assembly.GetAssembly(typeof(HelloMessageSerializer))!);
                ReceiptsMessageSerializer receiptsMessageSerializer = new(specProvider);
                serializationService.Register(receiptsMessageSerializer);
                serializationService.Register(new Network.P2P.Subprotocols.Eth.V66.Messages.ReceiptsMessageSerializer(receiptsMessageSerializer));

                return serializationService;
            })


            .AddSingleton<IProtocolValidator, ProtocolValidator>()
            .AddSingleton<IPooledTxsRequestor, PooledTxsRequestor>()
            .AddSingleton<ForkInfo>()
            .AddSingleton<IGossipPolicy>(Policy.FullGossip)
            .AddSingleton<ISnapServer, IWorldStateManager>(stateProvider => stateProvider.SnapServer!)

            .AddKeyedSingleton<INetworkStorage>("PeersDb", (ctx) =>
            {
                IInitConfig initConfig = ctx.Resolve<IInitConfig>();
                ILogManager logManager = ctx.Resolve<ILogManager>();

                string dbName = "PeersDB";
                IFullDb peersDb = initConfig.DiagnosticMode == DiagnosticMode.MemDb
                    ? new MemDb(dbName)
                    : new SimpleFilePublicKeyDb(dbName, InitializeNetwork.PeersDbPath.GetApplicationResourcePath(initConfig.BaseDbPath),
                        logManager);
                return new NetworkStorage(peersDb, logManager);
            })

            .AddAdvance<ProtocolsManager>(cfg =>
            {
                cfg
                    .As<IProtocolsManager>()
                    .WithAttributeFiltering()
                    .SingleInstance()
                    .OnActivating((m) =>
                    {
                        ProtocolsManager protocolManager = m.Instance;
                        ISyncConfig syncConfig = m.Context.Resolve<ISyncConfig>();
                        IWorldStateManager worldStateManager = m.Context.Resolve<IWorldStateManager>();

                        if (syncConfig.SnapServingEnabled == true)
                        {
                            protocolManager.AddSupportedCapability(new Capability(Protocol.Snap, 1));
                        }

                        if (worldStateManager.HashServer is null)
                        {
                            protocolManager.RemoveSupportedCapability(new Capability(Protocol.NodeData, 1));
                        }

                    });
            })

            ;

        // TODO: Add `WorldStateManager.InitializeNetwork`.
    }
}
