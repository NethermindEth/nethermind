// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery;

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
                CurrentNodeId = new Node(masterNode, "127.0.0.1", 9999, true),
                KSize = discoveryConfig.BucketSize,
                Alpha = discoveryConfig.Concurrency,
                Beta = discoveryConfig.BitsPerHop,

                LookupFindNeighbourHardTimout = TimeSpan.FromMilliseconds(discoveryConfig.SendNodeTimeout), // TODO: This seems very low.
                RefreshPingTimeout = TimeSpan.FromMilliseconds(discoveryConfig.PongTimeout),
                BootNodes = bootNodes
            })
            .AddSingleton<IIteratorNodeLookup, IteratorNodeLookup>()
            .AddSingleton<KademliaDiscv4Adapter>()
            .AddSingleton<IKademliaDiscv4Adapter, KademliaDiscv4Adapter>()
            .Bind<IDiscoveryMsgListener, IKademliaDiscv4Adapter>()
            .AddSingleton<IKademliaMessageSender<PublicKey, Node>>(c => c.Resolve<IKademliaDiscv4Adapter>())
            .AddSingleton<DiscoveryPersistenceManager>()
            .AddSingleton<NettyDiscoveryHandler>()
            .AddSingleton<DiscoveryApp>();
    }
}

public class NodeNodeHashProvider : IKeyOperator<PublicKey, Node>
{
    public PublicKey GetKey(Node node)
    {
        return node.Id;
    }

    public ValueHash256 GetKeyHash(PublicKey key)
    {
        return key.Hash;
    }

    public PublicKey CreateRandomKeyAtDistance(ValueHash256 nodePrefix, int depth)
    {
        // Obviously, we can't generate this. So we just randomly pick something.
        // I guess we can brute force it if needed.
        Span<byte> randomBytes = new byte[64];
        Random.Shared.NextBytes(randomBytes);
        return new PublicKey(randomBytes);
    }
}
