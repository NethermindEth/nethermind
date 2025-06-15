// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Autofac;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Discv5;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.Discovery.Serializers;
using Nethermind.Network.Dns;
using Nethermind.Network.Enr;
using Nethermind.Network.StaticNodes;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Init.Modules;

public class DiscoveryModule(IInitConfig initConfig, INetworkConfig networkConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            // Enr discovery uses DNS to get some bootnodes.
            .AddSingleton<EnrDiscovery, IEthereumEcdsa, ILogManager>((ethereumEcdsa, logManager) =>
            {
                // I do not use the key here -> API is broken - no sense to use the node signer here
                NodeRecordSigner nodeRecordSigner = new(ethereumEcdsa, new PrivateKeyGenerator().Generate());
                EnrRecordParser enrRecordParser = new(nodeRecordSigner);
                return new EnrDiscovery(enrRecordParser, networkConfig, logManager); // initialize with a proper network
            })

            // Allow feeding discovery app bootnodes from enr. Need `Run` to be called.
            .AddSingleton<NodeSourceToDiscV4Feeder>()
            .AddKeyedSingleton<INodeSource>(NodeSourceToDiscV4Feeder.SourceKey, ctx => ctx.Resolve<EnrDiscovery>())

            // Uses by RPC also.
            .AddSingleton<IStaticNodesManager, ILogManager>((logManager) => new StaticNodesManager(initConfig.StaticNodesPath, logManager))
            // This load from file.
            .AddSingleton<NodesLoader>()

            .AddSingleton<ITrustedNodesManager, ILogManager>((logManager) =>
                new TrustedNodesManager(initConfig.TrustedNodesPath, logManager))

            .Bind<INodeSource, IStaticNodesManager>()

            // Used by NodesLoader, and ProtocolsManager which add entry on sync peer connected
            .AddNetworkStorage(DbNames.PeersDb, "peers")
            .Bind<INodeSource, NodesLoader>()
            .AddComposite<INodeSource, CompositeNodeSource>()

            // The actual thing that uses the INodeSource(s)
            .AddSingleton<IPeerPool, PeerPool>()
            .AddSingleton<IPeerManager, PeerManager>()

            // Some config migration
            .AddDecorator<INetworkConfig>((ctx, networkConfig) =>
            {
                ChainSpec chainSpec = ctx.Resolve<ChainSpec>();
                IDiscoveryConfig discoveryConfig = ctx.Resolve<IDiscoveryConfig>();

                // Was in `UpdateDiscoveryConfig` step.
                if (discoveryConfig.Bootnodes != string.Empty)
                {
                    if (chainSpec.Bootnodes.Length != 0)
                    {
                        discoveryConfig.Bootnodes += "," + string.Join(",", chainSpec.Bootnodes.Select(static bn => bn.ToString()));
                    }
                }
                else if (chainSpec.Bootnodes is not null)
                {
                    discoveryConfig.Bootnodes = string.Join(",", chainSpec.Bootnodes.Select(static bn => bn.ToString()));
                }

                if (networkConfig.DiscoveryDns == null)
                {
                    string chainName = BlockchainIds.GetBlockchainName(chainSpec!.NetworkId).ToLowerInvariant();
                    networkConfig.DiscoveryDns = $"all.{chainName}.ethdisco.net";
                }
                networkConfig.Bootnodes = discoveryConfig.Bootnodes;
                return networkConfig;
            })

            // Serializers
            // The `IPrivateKeyGenerator` here is not exactly a `generator`. It is used to pass the exact same
            // private key to the discovery message serializer to sign the message.
            .AddKeyedSingleton<IPrivateKeyGenerator>(IProtectedPrivateKey.NodeKey, ctx => new SameKeyGenerator(ctx.ResolveKeyed<IProtectedPrivateKey>(IProtectedPrivateKey.NodeKey).Unprotect()))
            .AddSingleton<INodeIdResolver, NodeIdResolver>()
            .AddMessageSerializer<PingMsg, PingMsgSerializer>()
            .AddMessageSerializer<PongMsg, PongMsgSerializer>()
            .AddMessageSerializer<FindNodeMsg, FindNodeMsgSerializer>()
            .AddMessageSerializer<NeighborsMsg, NeighborsMsgSerializer>()
            .AddMessageSerializer<EnrRequestMsg, EnrRequestMsgSerializer>()
            .AddMessageSerializer<EnrResponseMsg, EnrResponseMsgSerializer>()

            ;


        // Discovery app
        // Needed regardless if used or not because of StopAsync in runner.
        // The DiscV4/5 is in CompositeDiscoveryApp.
        if (!initConfig.DiscoveryEnabled)
            builder.AddSingleton<IDiscoveryApp, NullDiscoveryApp>();
        else
        {
            builder
                .AddSingleton<IDiscoveryApp, CompositeDiscoveryApp>()
                .AddSingleton<INodeRecordProvider, NodeRecordProvider>()

                .AddNetworkStorage(DbNames.DiscoveryNodes, "discoveryNodes")
                .AddSingleton<DiscoveryV5App>()

                .AddSingleton<INodeDistanceCalculator, NodeDistanceCalculator>()
                .AddSingleton<INodeTable, NodeTable>()
                .AddSingleton<IEvictionManager, EvictionManager>()
                .AddSingleton<INodeLifecycleManagerFactory, NodeLifecycleManagerFactory>()
                .AddSingleton<IDiscoveryManager, DiscoveryManager>()
                .AddSingleton<INodesLocator, NodesLocator>()
                .AddSingleton<DiscoveryPersistenceManager>()
                .AddSingleton<DiscoveryApp>()

                ;
        }


        if (!networkConfig.OnlyStaticPeers)
        {
            // These are INodeSource only if `OnlyStaticPeers` is false.
            builder.Bind<INodeSource, IDiscoveryApp>();
            if (networkConfig.EnableEnrDiscovery) builder.Bind<INodeSource, EnrDiscovery>();
        }
    }
}
