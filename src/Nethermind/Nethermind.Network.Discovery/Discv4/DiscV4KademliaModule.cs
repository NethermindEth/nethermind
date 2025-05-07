// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery;

public class DiscV4KademliaModule(NodeRecord selfNodeRecord, PublicKey masterNode, IReadOnlyList<Node> bootNodes): Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            .AddModule(new KademliaModule<PublicKey, Node>())
            .AddSingleton<INodeHashProvider<Node>, NodeNodeHashProvider>()
            .AddSingleton<IKeyOperator<PublicKey, Node>, NodeNodeHashProvider>()
            .AddSingleton(selfNodeRecord)
            .AddSingleton<KademliaNodeSource>()
            .AddSingleton(new KademliaConfig<Node>()
            {
                CurrentNodeId = new Node(masterNode, "127.0.0.1", 9999, true),
                BootNodes = bootNodes
            })
            .AddSingleton<IKademliaMessageSender<PublicKey, Node>, KademliaDiscv4Adapter>();
    }
}

public class NodeNodeHashProvider : INodeHashProvider<Node>, IKeyOperator<PublicKey, Node>
{
    public ValueHash256 GetHash(Node node)
    {
        return node.Id.Hash;
    }

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

