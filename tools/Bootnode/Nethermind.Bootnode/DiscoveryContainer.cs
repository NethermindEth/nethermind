// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Network.Discovery.Discv4.Serializers;
using Nethermind.Network.Discovery.Discv5;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Bootnode;

internal static class DiscoveryContainer
{
    public static async Task<IContainer> BuildAsync(
        BootnodeOptions options,
        ILogManager logManager,
        IProtectedPrivateKey nodeKey,
        IProcessExitSource processExitSource,
        BootnodeKademliaBucketRegistry bucketRegistry,
        CancellationToken cancellationToken)
    {
        NetworkConfig networkConfig = new()
        {
            DiscoveryPort = options.DiscoveryPort,
            P2PPort = 0,
            LocalIp = options.LocalIp,
            ExternalIp = options.ExternalIp,
            ExternalIpV4 = options.ExternalIpV4,
            ExternalIpV6 = options.ExternalIpV6,
            Bootnodes = NetworkNode.ParseNodes(options.Bootnodes, logManager.GetLogger("Nethermind.Bootnode.DiscoveryContainer")),
            MaxCandidatePeerCount = 10000
        };

        DiscoveryConfig discoveryConfig = new()
        {
            BucketSize = options.BucketSize,
            Concurrency = options.Concurrency,
            DiscoveryInterval = options.DiscoveryIntervalMs,
            DiscoveryVersion = options.DiscoveryVersion,
            ConcurrentDiscoveryJob = options.ActiveDiscovery ? options.ActiveDiscoveryJobs : 0,
            UseDefaultDiscv5Bootnodes = options.UseDefaultDiscv5Bootnodes
        };

        IPResolver ipResolver = new(networkConfig, logManager);
        IIPResolver.NethermindIp resolvedIp = await ipResolver.Resolve(cancellationToken);

        ContainerBuilder builder = new();
        builder.RegisterInstance(logManager).As<ILogManager>().SingleInstance();
        builder.RegisterInstance(networkConfig).As<INetworkConfig>().SingleInstance();
        builder.RegisterInstance(discoveryConfig).As<IDiscoveryConfig>().SingleInstance();
        builder.RegisterInstance(BootnodeForkInfo.Instance).As<IForkInfo>().SingleInstance();
        builder.RegisterInstance(processExitSource).As<IProcessExitSource>().SingleInstance();
        builder.RegisterInstance(Timestamper.Default).As<ITimestamper>().SingleInstance();
        builder.RegisterInstance(TimerFactory.Default).As<ITimerFactory>().SingleInstance();
        builder.RegisterInstance(nodeKey).Keyed<IProtectedPrivateKey>(IProtectedPrivateKey.NodeKey).SingleInstance();

        builder
            .AddSingleton<ICryptoRandom, CryptoRandom>()
            .AddSingleton<IEthereumEcdsa>(_ => new EthereumEcdsa(1))
            .Bind<IEcdsa, IEthereumEcdsa>()
            .AddKeyedSingleton<IPrivateKeyGenerator>(IProtectedPrivateKey.NodeKey, context =>
                new SameKeyGenerator(context.ResolveKeyed<IProtectedPrivateKey>(IProtectedPrivateKey.NodeKey).Unprotect()))
            .AddSingleton<INodeIdResolver, NodeIdResolver>()
            .AddSingleton<IMessageSerializationService, MessageSerializationService>()
            .AddSingleton<INodeStatsManager>(context => new NodeStatsManager(
                context.Resolve<ITimerFactory>(),
                context.Resolve<ILogManager>(),
                context.Resolve<INetworkConfig>().MaxCandidatePeerCount))
            .AddSingleton<IDiscoveryApp, CompositeDiscoveryApp>()
            .AddMessageSerializer<PingMsg, PingMsgSerializer>()
            .AddMessageSerializer<PongMsg, PongMsgSerializer>()
            .AddMessageSerializer<FindNodeMsg, FindNodeMsgSerializer>()
            .AddMessageSerializer<NeighborsMsg, NeighborsMsgSerializer>()
            .AddMessageSerializer<EnrRequestMsg, EnrRequestMsgSerializer>()
            .AddMessageSerializer<EnrResponseMsg, EnrResponseMsgSerializer>();

        builder.RegisterInstance(ipResolver).As<IIPResolver>().SingleInstance();

        builder.Register(context =>
            CreateEnode(context.ResolveKeyed<IProtectedPrivateKey>(IProtectedPrivateKey.NodeKey), networkConfig, resolvedIp))
            .As<IEnode>()
            .SingleInstance();

        builder.RegisterType<BootnodeNodeRecordProvider>()
            .As<INodeRecordProvider>()
            .WithParameter(ResolvedParameter.ForKeyed<IProtectedPrivateKey>(IProtectedPrivateKey.NodeKey))
            .WithParameter(new TypedParameter(typeof(IIPResolver.NethermindIp), resolvedIp))
            .WithParameter(new NamedParameter("dataDir", options.DataDir))
            .SingleInstance();

        builder.RegisterInstance(new FileNetworkStorage(Path.Combine(options.DataDir, "discovery-v4-nodes.json"), logManager))
            .Keyed<INetworkStorage>(DbNames.DiscoveryNodes)
            .SingleInstance();

        builder.RegisterInstance(new FileNetworkStorage(Path.Combine(options.DataDir, "discovery-v5-nodes.json"), logManager))
            .Keyed<INetworkStorage>(DbNames.DiscoveryV5Nodes)
            .SingleInstance();

        builder.RegisterType<DiscoveryV5App>()
            .AsSelf()
            .WithAttributeFiltering()
            .WithParameter(new TypedParameter(
                typeof(Action<ContainerBuilder>),
                (Action<ContainerBuilder>)(discv5Builder =>
                {
                    discv5Builder.RegisterType<BootnodeDiscoveryV5NodeSource>()
                        .As<IKademliaNodeSource>()
                        .SingleInstance();
                    RegisterBucketStats(discv5Builder, "discv5", bucketRegistry);
                })))
            .SingleInstance()
            .ExternallyOwned();
        builder.RegisterType<DiscoveryApp>()
            .AsSelf()
            .WithParameter(new TypedParameter(
                typeof(Action<ContainerBuilder>),
                (Action<ContainerBuilder>)(discv4Builder => RegisterBucketStats(discv4Builder, "discv4", bucketRegistry))))
            .SingleInstance()
            .ExternallyOwned();

        return builder.Build();
    }

    private static void RegisterBucketStats(ContainerBuilder builder, string protocol, BootnodeKademliaBucketRegistry registry)
    {
        builder.Register(context => new BootnodeKademliaBucketSource(
                protocol,
                context.Resolve<IRoutingTable<Node, ValueHash256>>()))
            .As<IBootnodeKademliaBucketSource>()
            .SingleInstance();

        builder.RegisterBuildCallback(scope =>
            registry.Register(scope.Resolve<IBootnodeKademliaBucketSource>()));
    }

    private static IEnode CreateEnode(IProtectedPrivateKey nodeKey, INetworkConfig networkConfig, IIPResolver.NethermindIp resolvedIp) =>
        new Enode(nodeKey.PublicKey, resolvedIp.ExternalIp, networkConfig.P2PPort, networkConfig.DiscoveryPort);

    private sealed class BootnodeForkInfo : IForkInfo
    {
        public static BootnodeForkInfo Instance { get; } = new();

        public ForkId GetForkId(ulong headNumber, ulong headTimestamp) => new(0, 0);

        public Nethermind.Network.ValidationResult ValidateForkId(ForkId peerId, BlockHeader? head) => Nethermind.Network.ValidationResult.Valid;

        public bool IsForkIdCompatible(ForkId peerId) => true;

        public ForkActivationsSummary GetForkActivationsSummary(BlockHeader? head) => default;
    }
}
