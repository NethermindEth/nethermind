// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Kademlia;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Discv4.Kademlia;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Test;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

/// <summary>
/// Drives the real discv4 module wiring (routing table, limiter and <see cref="NodeSource"/>) with bonded pings, so
/// the subnet cap is exercised through the same path that admits peers in production.
/// </summary>
[Parallelizable(ParallelScope.Self)]
public class KademliaModuleTests
{
    private const int FillerCount = 8;
    private const int TargetCount = 10;
    private const int TargetBucketLimit = 2;

    private static readonly IPEndPoint SelfEndpoint = new(IPAddress.Loopback, 30303);

    [Test]
    [CancelAfter(30000)]
    public async Task Two_per_bucket_and_ten_per_table_reach_the_table_and_the_candidate_stream(CancellationToken token)
    {
        (List<PublicKey> fillers, List<PublicKey> targets, PublicKey rejected) = SelectPeers();
        Dictionary<PublicKey, IPEndPoint> endpoints = [];

        await using IContainer container = BuildContainer();
        container.Resolve<NetworkListenerState>().SetDiscoveryAddress(IPAddress.Loopback);
        container.Resolve<NetworkListenerState>().SetRlpxAddress(IPAddress.Loopback);
        IRoutingTable<Node, ValueHash256> table = container.Resolve<IRoutingTable<Node, ValueHash256>>();
        IKademliaNodeSource nodeSource = container.Resolve<IKademliaNodeSource>();
        IKademliaAdapter adapter = container.Resolve<IKademliaAdapter>();
        IMessageSerializationService serializer = new SerializationBuilder().WithDiscovery(TestItem.PrivateKeyA).TestObject;
        Dictionary<IPEndPoint, PublicKey> idsByEndpoint = [];
        RespondingMsgSender sender = new(adapter, container.Resolve<ITimestamper>(), serializer, idsByEndpoint);
        adapter.MsgSender = sender;

        await BondAndInject(adapter, fillers, i => new IPEndPoint(IPAddress.Parse($"203.0.{i + 1}.1"), 30303), endpoints, idsByEndpoint);
        await BondAndInject(adapter, targets, i => new IPEndPoint(IPAddress.Parse($"192.0.2.{i + 1}"), 30303), endpoints, idsByEndpoint);
        await BondAndInject(adapter, [rejected], _ => new IPEndPoint(IPAddress.Parse("192.0.2.250"), 30303), endpoints, idsByEndpoint);

        Node[] tableNodes = [.. table.IterateBuckets().SelectMany(static bucket => bucket.Nodes)];
        PublicKey[] targetIds = [.. targets];
        Node[] tableTargets = [.. tableNodes.Where(node => targetIds.Contains(node.Id))];

        Assert.That(container.Resolve<KademliaConfig<Node>>().NodeLimiter, Is.Not.Null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tableTargets, Has.Length.EqualTo(TargetCount));
            Assert.That(tableTargets.Any(node => node.Id == rejected), Is.False);
            Assert.That(
                table.IterateBuckets().Max(bucket => bucket.Nodes.Count(node => targetIds.Contains(node.Id))),
                Is.LessThanOrEqualTo(TargetBucketLimit));
        }

        using CancellationTokenSource readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        readCts.CancelAfter(TimeSpan.FromSeconds(5));
        List<Node> candidates = await nodeSource.DiscoverNodes(readCts.Token)
            .Take(FillerCount + TargetCount)
            .ToListAsync(readCts.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(candidates.Where(node => targetIds.Contains(node.Id)).Select(static node => node.Id).ToHashSet(),
                Is.EquivalentTo(targetIds));
            Assert.That(candidates.Any(node => node.Id == rejected), Is.False);
        }

        sender.Sent.Clear();
        FindNodeMsg stillAnswered = new(rejected, long.MaxValue / 2, TestItem.PublicKeyA.Bytes)
        {
            FarAddress = endpoints[rejected]
        };

        await adapter.OnIncomingMsg(stillAnswered);

        Assert.That(sender.Sent.OfType<NeighborsMsg>().Any(msg => endpoints[rejected].Equals(msg.FarAddress)), Is.True);
    }

    private static async Task BondAndInject(
        IKademliaAdapter adapter,
        IReadOnlyList<PublicKey> nodes,
        Func<int, IPEndPoint> endpointAt,
        Dictionary<PublicKey, IPEndPoint> endpoints,
        Dictionary<IPEndPoint, PublicKey> idsByEndpoint)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            PublicKey id = nodes[i];
            IPEndPoint endpoint = endpointAt(i);
            endpoints[id] = endpoint;
            idsByEndpoint[endpoint] = id;

            PingMsg ping = new(
                id,
                long.MaxValue / 2,
                endpoint,
                SelfEndpoint,
                TestItem.KeccakA.ValueHash256,
                sourceTcpPort: 30303,
                destinationTcpPort: 30303)
            {
                FarAddress = endpoint
            };

            await adapter.OnIncomingMsg(ping);
        }
    }

    private static (List<PublicKey> Fillers, List<PublicKey> Targets, PublicKey Rejected) SelectPeers()
    {
        // Bucket placement follows the Keccak hash, so pick keys that share few top bits: fillers seed every
        // three-bit prefix and the targets fill the table two per bucket in five of them.
        Dictionary<int, List<PublicKey>> byPrefix = Enumerable.Range(1, TestItem.PrivateKeys.Length - 1)
            .Select(index => TestItem.PrivateKeys[index].PublicKey)
            .GroupBy(TopThreeBits)
            .ToDictionary(group => group.Key, group => group.ToList());

        List<PublicKey> fillers = [];
        for (int prefix = 0; prefix < FillerCount; prefix++)
        {
            fillers.Add(byPrefix[prefix][0]);
        }

        List<PublicKey> targets = [];
        for (int prefix = 0; prefix < TargetCount / TargetBucketLimit; prefix++)
        {
            targets.AddRange(byPrefix[prefix].Skip(1).Take(TargetBucketLimit));
        }

        PublicKey rejected = byPrefix[TargetCount / TargetBucketLimit].Skip(1).First();
        return (fillers, targets, rejected);
    }

    private static int TopThreeBits(PublicKey key) => key.Hash.ValueHash256.Bytes[0] >> 5;

    private static IContainer BuildContainer()
    {
        NetworkConfig networkConfig = new()
        {
            LocalIp = "127.0.0.1",
            ExternalIp = "127.0.0.1",
            DiscoveryPort = 30303,
            P2PPort = 30303
        };
        DiscoveryConfig discoveryConfig = new()
        {
            BucketSize = 2,
            BitsPerHop = 32,
            ConcurrentDiscoveryJob = 1,
            UseDefaultDiscv5Bootnodes = false
        };
        Node currentNode = new(TestItem.PublicKeyA, IPAddress.Loopback.ToString(), 30303);

        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        forkInfo.IsForkIdCompatible(Arg.Any<ForkId>()).Returns(true);

        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new TestNethermindModule(networkConfig, discoveryConfig))
            .AddModule(new KademliaModule(currentNode, []));
        builder.RegisterInstance(forkInfo).As<IForkInfo>();
        return builder.Build();
    }

    private sealed class RespondingMsgSender(
        IKademliaAdapter adapter,
        ITimestamper timestamper,
        IMessageSerializationService serializer,
        Dictionary<IPEndPoint, PublicKey> idsByEndpoint) : IMsgSender
    {
        public List<DiscoveryMsg> Sent { get; } = [];

        public Task SendMsg(DiscoveryMsg discoveryMsg)
        {
            Sent.Add(discoveryMsg);
            if (discoveryMsg is not PingMsg ping) return Task.CompletedTask;

            // Serializing fills in the ping's MDC, which the bond handshake echoes back in the pong.
            serializer.SerializeToArray(ping);
            PublicKey id = idsByEndpoint[ping.FarAddress!];
            PongMsg pong = new(id, timestamper.UnixTime.SecondsLong + 1, ping.Mdc!.Value)
            {
                FarAddress = ping.FarAddress
            };
            return adapter.OnIncomingMsg(pong);
        }
    }
}
