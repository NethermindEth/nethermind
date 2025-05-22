// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv4;

public class DiscV4KademliaModule(PublicKey masterNode, IReadOnlyList<Node> bootNodes) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            .AddModule(new KademliaModule<PublicKey, Node>())
            .AddSingleton<IKeyOperator<PublicKey, Node>, NodeNodeHashProvider>()
            .AddSingleton<IKademliaNodeSource, KademliaNodeSource>()
            .AddSingleton<KademliaConfig<Node>, IDiscoveryConfig>((discoveryConfig) => new KademliaConfig<Node>()
            {
                CurrentNodeId = new Node(masterNode, "127.0.0.1", 9999, true), // It actually only need masterNode.
                KSize = discoveryConfig.BucketSize,
                Alpha = discoveryConfig.Concurrency,
                Beta = discoveryConfig.BitsPerHop,

                LookupFindNeighbourHardTimout = TimeSpan.FromMilliseconds(discoveryConfig.SendNodeTimeout), // TODO: This seems very low.
                RefreshPingTimeout = TimeSpan.FromMilliseconds(discoveryConfig.PingTimeout),
                BootNodes = bootNodes
            })
            .AddSingleton<IIteratorNodeLookup, IteratorNodeLookup>()
            .AddSingleton<IKademliaDiscv4Adapter, KademliaDiscv4Adapter>()
            .Bind<IDiscoveryMsgListener, IKademliaDiscv4Adapter>()
            .Bind<IKademliaMessageSender<PublicKey, Node>, IKademliaDiscv4Adapter>()
            .AddSingleton<DiscoveryPersistenceManager>()
            .AddSingleton<NettyDiscoveryHandler>()
            .AddSingleton<DiscoveryApp>();
    }
}
