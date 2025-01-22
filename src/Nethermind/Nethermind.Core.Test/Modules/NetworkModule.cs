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
using Nethermind.Network.Discovery;
using Nethermind.Network.Dns;
using Nethermind.Network.Enr;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Network.StaticNodes;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.State.SnapServer;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.TxPool;

namespace Nethermind.Core.Test.Modules;

public class NetworkModule(IInitConfig initConfig, INetworkConfig networkConfig) : Module
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

            ;

        // TODO: Add `WorldStateManager.InitializeNetwork`.

        ConfigureDiscover(builder);
    }

    private void ConfigureDiscover(ContainerBuilder builder)
    {
        // Discovery app
        if (!initConfig.DiscoveryEnabled)
        {
            builder.AddSingleton<IDiscoveryApp, NullDiscoveryApp>();
        }
        else
        {
            builder.AddSingleton<IDiscoveryApp, CompositeDiscoveryApp>();
        }

        if (!networkConfig.OnlyStaticPeers)
        {
            builder
                // Enr discovery
                .AddDecorator<INetworkConfig>((ctx, networkConfig) =>
                {
                    ChainSpec chainSpec = ctx.Resolve<ChainSpec>();
                    if (networkConfig.DiscoveryDns == null)
                    {
                        string chainName = BlockchainIds.GetBlockchainName(chainSpec!.NetworkId).ToLowerInvariant();
                        networkConfig.DiscoveryDns = $"all.{chainName}.ethdisco.net";
                    }
                    return networkConfig;
                })
                .AddSingleton<INodeSource>((ctx) =>
                {
                    IEthereumEcdsa ethereumEcdsa = ctx.Resolve<IEthereumEcdsa>();
                    ILogManager logManager = ctx.Resolve<ILogManager>();

                    // I do not use the key here -> API is broken - no sense to use the node signer here
                    NodeRecordSigner nodeRecordSigner = new(ethereumEcdsa, new PrivateKeyGenerator().Generate());
                    EnrRecordParser enrRecordParser = new(nodeRecordSigner);
                    return new EnrDiscovery(enrRecordParser, networkConfig, logManager); // initialize with a proper network
                })

                // TODO: Node source to discovery 4 feeder.

                // Connect discovery app.
                .Bind<INodeSource, IDiscoveryApp>();
        }

        builder

            // Node source
            .AddSingleton<IStaticNodesManager, ILogManager>((logManager) => new StaticNodesManager(initConfig.StaticNodesPath, logManager))
            .Bind<INodeSource, IStaticNodesManager>()

            // another node source
            .AddSingleton<NodesLoader>()
            .Bind<INodeSource, NodesLoader>()

            // composite node source
            .AddComposite<INodeSource, CompositeNodeSource>()

            // The actual thing that uses the INodeSource(s)
            .AddSingleton<IPeerPool, PeerPool>()
            .AddSingleton<IPeerManager, PeerManager>()
            ;
    }
}
