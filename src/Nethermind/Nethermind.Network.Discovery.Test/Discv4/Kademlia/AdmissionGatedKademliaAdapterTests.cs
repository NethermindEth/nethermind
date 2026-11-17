// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Discv4.Kademlia;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4.Kademlia;

public class AdmissionGatedKademliaAdapterTests
{
    [Test]
    public async Task Filtered_ping_is_forwarded_to_inner_so_the_handshake_completes()
    {
        (AdmissionGatedKademliaAdapter gate, IKademliaAdapter inner, DiscoveryNodeAdmission admission) = CreateGate();
        admission.TryAdmit(CreateNode(TestItem.PublicKeyA, "203.0.113.10"));
        PingMsg ping = CreatePing(TestItem.PublicKeyB, "203.0.113.20");

        await gate.OnIncomingMsg(ping);

        // The peer still needs our Pong to prove our endpoint, so admission must not suppress the message.
        await inner.Received(1).OnIncomingMsg(ping);
    }

    [TestCase(MsgType.Pong)]
    [TestCase(MsgType.Neighbors)]
    public async Task Unsolicited_response_is_left_to_inner_and_does_not_consume_admission(MsgType msgType)
    {
        (AdmissionGatedKademliaAdapter gate, IKademliaAdapter inner, DiscoveryNodeAdmission admission) = CreateGate();

        await gate.OnIncomingMsg(CreateResponse(msgType, TestItem.PublicKeyB, "203.0.113.20"));

        // The inner adapter correlates and discards it. The gate must not reserve the source subnet for a
        // message that was never correlated.
        await inner.Received(1).OnIncomingMsg(Arg.Any<DiscoveryMsg>());
        Assert.That(admission.TryAdmit(CreateNode(TestItem.PublicKeyC, "203.0.113.20")), Is.True);
    }

    [Test]
    public async Task Accepted_ping_is_forwarded_to_inner()
    {
        (AdmissionGatedKademliaAdapter gate, IKademliaAdapter inner, _) = CreateGate();
        PingMsg ping = CreatePing(TestItem.PublicKeyA, "203.0.113.10");

        await gate.OnIncomingMsg(ping);

        await inner.Received(1).OnIncomingMsg(ping);
    }

    [Test]
    public async Task Malformed_ping_is_forwarded_for_the_adapter_to_reject_without_consuming_admission()
    {
        (AdmissionGatedKademliaAdapter gate, IKademliaAdapter inner, DiscoveryNodeAdmission admission) = CreateGate();
        PingMsg malformed = CreatePing(TestItem.PublicKeyA, "203.0.113.10");
        malformed.FarAddress = null;

        await gate.OnIncomingMsg(malformed);

        await inner.Received(1).OnIncomingMsg(malformed);
        // The malformed ping did not record its subnet.
        Assert.That(admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "203.0.113.20")), Is.True);
    }

    [Test]
    public async Task ReadPeerCandidates_admits_bonded_candidates_and_filters_reserved_subnets()
    {
        (AdmissionGatedKademliaAdapter gate, IKademliaAdapter inner, DiscoveryNodeAdmission admission) = CreateGate();
        Node admittedNode = CreateNode(TestItem.PublicKeyA, "203.0.114.10");
        Node filteredNode = CreateNode(TestItem.PublicKeyB, "203.0.113.20");
        admission.TryAdmit(CreateNode(TestItem.PublicKeyC, "203.0.113.10"));
        inner.ReadPeerCandidates(Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([admittedNode, filteredNode]));

        List<Node> received = [];
        await foreach (Node node in gate.ReadPeerCandidates(CancellationToken.None))
        {
            received.Add(node);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(received, Has.Count.EqualTo(1));
            Assert.That(received[0], Is.SameAs(admittedNode));
            // The candidate stream admits a bonded node from a fresh subnet on its own.
            Assert.That(admission.TryAdmit(CreateNode(TestItem.PublicKeyE, "203.0.114.20")), Is.False);
        }

        // The filtered candidate's publish reservation is released; an admitted one keeps it to stay deduplicated.
        inner.Received(1).ReleasePeerCandidate(filteredNode);
        inner.DidNotReceive().ReleasePeerCandidate(admittedNode);
    }

    [Test]
    public async Task Outbound_requests_are_forwarded_regardless_of_admission()
    {
        (AdmissionGatedKademliaAdapter gate, IKademliaAdapter inner, DiscoveryNodeAdmission admission) = CreateGate();
        admission.TryAdmit(CreateNode(TestItem.PublicKeyA, "203.0.113.10"));
        Node receiver = CreateNode(TestItem.PublicKeyB, "203.0.113.20");
        CancellationToken token = CancellationToken.None;
        Node[] neighbours = [CreateNode(TestItem.PublicKeyC, "203.0.114.20")];
        inner.Ping(receiver, token).Returns(true);
        inner.FindNeighbours(receiver, TestItem.PublicKeyD, token).Returns(neighbours);
        inner.SendEnrRequest(receiver, token).Returns((EnrResponseMsg?)null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await gate.Ping(receiver, token), Is.True);
            Assert.That(await gate.FindNeighbours(receiver, TestItem.PublicKeyD, token), Is.SameAs(neighbours));
            Assert.That(await gate.SendEnrRequest(receiver, token), Is.Null);
        }

        await inner.Received(1).Ping(receiver, token);
        await inner.Received(1).FindNeighbours(receiver, TestItem.PublicKeyD, token);
        await inner.Received(1).SendEnrRequest(receiver, token);
    }

    [Test]
    public async Task Outbound_requests_to_an_admitted_receiver_are_forwarded()
    {
        (AdmissionGatedKademliaAdapter gate, IKademliaAdapter inner, DiscoveryNodeAdmission admission) = CreateGate();
        Node receiver = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        admission.TryAdmit(receiver);
        CancellationToken token = CancellationToken.None;
        Node[] neighbours = [CreateNode(TestItem.PublicKeyB, "203.0.114.20")];
        inner.Ping(receiver, token).Returns(true);
        inner.FindNeighbours(receiver, TestItem.PublicKeyC, token).Returns(neighbours);
        inner.SendEnrRequest(receiver, token).Returns((EnrResponseMsg?)null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await gate.Ping(receiver, token), Is.True);
            Assert.That(await gate.FindNeighbours(receiver, TestItem.PublicKeyC, token), Is.SameAs(neighbours));
            Assert.That(await gate.SendEnrRequest(receiver, token), Is.Null);
        }

        await inner.Received(1).Ping(receiver, token);
        await inner.Received(1).FindNeighbours(receiver, TestItem.PublicKeyC, token);
        await inner.Received(1).SendEnrRequest(receiver, token);
    }

    [Test]
    public void MsgSender_forwards_to_inner()
    {
        (AdmissionGatedKademliaAdapter gate, IKademliaAdapter inner, _) = CreateGate();
        IMsgSender sender = Substitute.For<IMsgSender>();

        gate.MsgSender = sender;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(gate.MsgSender, Is.SameAs(sender));
            Assert.That(inner.MsgSender, Is.SameAs(sender));
        }
    }

    [Test]
    public async Task DisposeAsync_forwards_to_inner()
    {
        (AdmissionGatedKademliaAdapter gate, IKademliaAdapter inner, _) = CreateGate();

        await gate.DisposeAsync();

        await inner.Received(1).DisposeAsync();
    }

    [Test]
    public async Task Disabled_admission_forwards_everything()
    {
        (AdmissionGatedKademliaAdapter gate, IKademliaAdapter inner, _) = CreateGate(enabled: false);
        Node node = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        PingMsg ping = CreatePing(TestItem.PublicKeyA, "203.0.113.10");
        CancellationToken token = CancellationToken.None;
        inner.Ping(node, token).Returns(true);

        await gate.OnIncomingMsg(ping);
        Assert.That(await gate.Ping(node, token), Is.True);

        await inner.Received(1).OnIncomingMsg(ping);
        await inner.Received(1).Ping(node, token);
    }

    private static (AdmissionGatedKademliaAdapter Gate, IKademliaAdapter Inner, DiscoveryNodeAdmission Admission)
        CreateGate(bool enabled = true, bool subnetBucketing = true)
    {
        IRoutingTable<Node, ValueHash256> routingTable = Substitute.For<IRoutingTable<Node, ValueHash256>>();
        INetworkStorage storage = Substitute.For<INetworkStorage>();
        if (enabled)
        {
            storage.GetPersistedNodes().Returns([]);
        }

        KademliaConfig<Node> kademliaConfig = new()
        {
            CurrentNodeId = CreateNode(TestItem.PublicKeyD, "198.51.100.2"),
        };
        NodeFilter filter = NodeFilter.Create(100, enabled, subnetBucketing, IPAddress.Parse("198.51.100.1"));
        DiscoveryNodeAdmission admission = new(
            filter,
            enabled,
            routingTable,
            kademliaConfig,
            storage,
            admittedCapacity: 100,
            LimboLogs.Instance);
        IKademliaAdapter inner = Substitute.For<IKademliaAdapter>();
        inner.OnIncomingMsg(Arg.Any<DiscoveryMsg>()).Returns(Task.CompletedTask);
        return (new AdmissionGatedKademliaAdapter(inner, admission, LimboLogs.Instance), inner, admission);
    }

    private static DiscoveryMsg CreateResponse(MsgType msgType, PublicKey id, string host)
    {
        IPEndPoint address = new(IPAddress.Parse(host), 30303);
        DiscoveryMsg msg = msgType switch
        {
            MsgType.Pong => new PongMsg(id, 1000, default),
            MsgType.Neighbors => new NeighborsMsg(id, 1000, ArraySegment<Node>.Empty),
            _ => throw new ArgumentOutOfRangeException(nameof(msgType)),
        };
        msg.FarAddress = address;
        return msg;
    }

    private static PingMsg CreatePing(PublicKey id, string host)
    {
        IPEndPoint address = new(IPAddress.Parse(host), 30303);
        PingMsg ping = new(id, 1000, address, address, new byte[32]);
        ping.FarAddress = address;
        return ping;
    }

    private static async IAsyncEnumerable<Node> ToAsyncEnumerable(
        IEnumerable<Node> nodes,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        foreach (Node node in nodes)
        {
            token.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return node;
        }
    }

    private static Node CreateNode(PublicKey id, string host) => new(id, host, 30303, 30303);
}
