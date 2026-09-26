// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Discv5;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Network.Discovery.Discv4.Serializers;
using Nethermind.Network.Dns;
using Nethermind.Network.Enr;
using Nethermind.Network.StaticNodes;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Init.Modules;

public class DiscoveryModule(IInitConfig initConfig, INetworkConfig networkConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.Register(static context => new NodesLoaderOptions(
                LoadBootnodesAsPeerCandidates: (context.Resolve<IDiscoveryConfig>().DiscoveryVersion & DiscoveryVersion.V4) != 0))
            .SingleInstance();

        builder.RegisterType<NodesLoader>()
            .AsSelf()
            .WithAttributeFiltering()
            .SingleInstance();

        builder
            // Enr discovery uses DNS to get some bootnodes.
            .AddSingleton<EnrDiscovery, IEthereumEcdsa, IForkInfo, ILogManager>((ethereumEcdsa, forkInfo, logManager) =>
                CreateEnrDiscovery(ethereumEcdsa, forkInfo, logManager))

            // Allow feeding discovery app bootnodes from enr. Need `Run` to be called.
            .AddSingleton<NodeSourceToDiscV4Feeder>()
            .AddKeyedSingleton<INodeSource>(NodeSourceToDiscV4Feeder.SourceKey, ctx => ctx.Resolve<EnrDiscovery>())

            // Uses by RPC also.
            .AddSingleton<IStaticNodesManager, ILogManager>(logManager =>
                new StaticNodesManager(initConfig.StaticNodesPath.GetApplicationResourcePath(initConfig.DataDir), logManager))
            // This load from file.
            .AddSingleton<ITrustedNodesManager, ILogManager>((logManager) =>
                new TrustedNodesManager(initConfig.TrustedNodesPath.GetApplicationResourcePath(initConfig.DataDir), logManager))

            .Bind<INodeSource, IStaticNodesManager>()
            .Bind<INodeSource, ITrustedNodesManager>()

            // Used by NodesLoader, and ProtocolsManager which add entry on sync peer connected
            .AddNetworkStorage(DbNames.PeersDb, DbNames.PeersDb)
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

                if (networkConfig.DiscoveryDns == null)
                {
                    string chainName = BlockchainIds.GetBlockchainName(chainSpec!.NetworkId).ToLowerInvariant();
                    networkConfig.DiscoveryDns = $"all.{chainName}.ethdisco.net";
                }

                networkConfig.Bootnodes = [.. networkConfig.Bootnodes, .. discoveryConfig.Bootnodes, .. chainSpec.Bootnodes];

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

                .AddNetworkStorage(DbNames.DiscoveryNodes, DbNames.DiscoveryNodes)
                .AddNetworkStorage(DbNames.DiscoveryV5Nodes, DbNames.DiscoveryV5Nodes)

                ;

            // DiscoveryApp and DiscoveryV5App implement IStoppableService via IDiscoveryApp,
            // but their lifecycle is owned by CompositeDiscoveryApp. Mark ExternallyOwned so
            // ServiceStopperMiddleware does not double-stop them.
            builder.RegisterType<DiscoveryV5App>().AsSelf().WithAttributeFiltering().SingleInstance().ExternallyOwned();
            builder.RegisterType<DiscoveryApp>().AsSelf().WithAttributeFiltering().SingleInstance().ExternallyOwned();
        }


        if (!networkConfig.OnlyStaticPeers)
        {
            // These are INodeSource only if `OnlyStaticPeers` is false.
            builder.Bind<INodeSource, IDiscoveryApp>();
            if (networkConfig.EnableEnrDiscovery) builder.Bind<INodeSource, EnrDiscovery>();
        }
    }

    private EnrDiscovery CreateEnrDiscovery(IEthereumEcdsa ethereumEcdsa, IForkInfo forkInfo, ILogManager logManager)
    {
        NodeRecordSigner nodeRecordSigner = new(ethereumEcdsa, new PrivateKeyGenerator().Generate());
        EnrRecordParser enrRecordParser = new(nodeRecordSigner);
        return new EnrDiscovery(enrRecordParser, networkConfig, forkInfo, logManager);
    }
}
