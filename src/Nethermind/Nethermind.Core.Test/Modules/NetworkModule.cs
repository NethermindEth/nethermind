// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.State;
using Nethermind.State.SnapServer;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Trie;
using Nethermind.TxPool;

namespace Nethermind.Core.Test.Modules;

public class NetworkModule(IInitConfig initConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<IBetterPeerStrategy, TotalDifficultyBetterPeerStrategy>()
            .AddSingleton<IPivot, Pivot>()
            .AddSingleton<IFullStateFinder, FullStateFinder>()
            .AddSingleton<INodeStatsManager, NodeStatsManager>()
            .AddSingleton<IIPResolver, IPResolver>()
            .AddSingleton<IBeaconSyncStrategy>(No.BeaconSync)

            .AddSingleton<IDisconnectsAnalyzer, MetricsDisconnectsAnalyzer>()
            .AddSingleton<ISessionMonitor, SessionMonitor>()
            .AddSingleton<IRlpxHost, RlpxHost>()
            .AddSingleton<IHandshakeService, HandshakeService>()

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
            .AddComposite<ITxGossipPolicy, CompositeTxGossipPolicy>()

            .OnActivate<ISyncPeerPool>((peerPool, ctx) =>
            {
                ILogManager logManager = ctx.Resolve<ILogManager>();
                ctx.Resolve<IWorldStateManager>().InitializeNetwork(new PathNodeRecovery(peerPool, logManager));
            })

            // TODO: LastNStateRootTracker
            .AddSingleton<ISnapServer, IWorldStateManager>(stateProvider => stateProvider.SnapServer!)

            .AddKeyedSingleton<INetworkStorage>(INetworkStorage.PeerDb, (ctx) =>
            {
                ILogManager logManager = ctx.Resolve<ILogManager>();

                string dbName = INetworkStorage.PeerDb;
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

            // Some config migration
            .AddDecorator<INetworkConfig>((ctx, networkConfig) =>
            {
                ILogManager logManager = ctx.Resolve<ILogManager>();
                if (networkConfig.DiagTracerEnabled)
                {
                    NetworkDiagTracer.IsEnabled = true;
                }
                if (NetworkDiagTracer.IsEnabled)
                {
                    NetworkDiagTracer.Start(logManager);
                }
                int maxPeersCount = networkConfig.ActivePeersMaxCount;
                Network.Metrics.PeerLimit = maxPeersCount;
                return networkConfig;
            })

            ;
    }

}
