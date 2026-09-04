// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Runtime.CompilerServices;
using Nethermind.Config;
using Nethermind.Crypto;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Bootnode.Test;

[TestFixture]
public class DiscoveredNodeStoreTests
{
    [Test]
    public void Configured_node_becomes_active_under_discovered_protocol()
    {
        using PrivateKeyGenerator generator = new();
        using PrivateKey privateKey = generator.Generate();
        NetworkNode networkNode = new(privateKey.PublicKey, "127.0.0.1", 30303);
        Node node = new(networkNode);
        DiscoveredNodeStore store = new();

        DiscoverySnapshot configuredSnapshot = store.AddConfiguredBootnodes([networkNode]);
        store.AddOrUpdate(node, "discv4", isActive: true);
        DiscoverySnapshot activeSnapshot = store.CreateSnapshot();
        NodeDto activeNode = store.GetActiveNodes().Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(configuredSnapshot.AllConfiguredCount, Is.EqualTo(1));
            Assert.That(configuredSnapshot.ActiveConfiguredCount, Is.Zero);
            Assert.That(activeSnapshot.ActiveCount, Is.EqualTo(1));
            Assert.That(activeSnapshot.AllCount, Is.EqualTo(1));
            Assert.That(activeSnapshot.ActiveDiscv4Count, Is.EqualTo(1));
            Assert.That(activeSnapshot.AllConfiguredCount, Is.Zero);
            Assert.That(activeNode.IsBootnode, Is.True);
            Assert.That(activeNode.Enode, Is.EqualTo(node.ToString(Node.Format.ENode)));
        }
    }

    [Test]
    public void Node_dto_reports_tcp_and_discovery_ports()
    {
        using PrivateKeyGenerator generator = new();
        using PrivateKey privateKey = generator.Generate();
        Node node = Node.FromDiscoveryEndpoint(privateKey.PublicKey, new IPEndPoint(IPAddress.Loopback, 30303));
        DiscoveredNodeStore store = new();

        store.AddOrUpdate(node, "discv4", isActive: true);
        NodeDto result = store.GetActiveNodes().Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TcpPort, Is.Zero);
            Assert.That(result.DiscoveryPort, Is.EqualTo(30303));
        }
    }

    [TestCase("127.0.0.1")]
    [TestCase("::ffff:127.0.0.1")]
    [TestCase("2001:db8::1")]
    public void Node_dto_host_matches_node_host(string address)
    {
        using PrivateKeyGenerator generator = new();
        using PrivateKey privateKey = generator.Generate();
        Node node = Node.FromDiscoveryEndpoint(
            privateKey.PublicKey,
            new IPEndPoint(IPAddress.Parse(address), 30303));
        DiscoveredNodeStore store = new();

        store.AddOrUpdate(node, "discv4", isActive: true);

        Assert.That(store.GetActiveNodes().Single().Host, Is.EqualTo(node.Host));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Retention_limit_prunes_inactive_nodes_before_active_nodes(bool deactivateAfterAdd)
    {
        using PrivateKeyGenerator generator = new();
        using PrivateKey firstKey = generator.Generate();
        using PrivateKey secondKey = generator.Generate();
        using PrivateKey thirdKey = generator.Generate();
        Node firstNode = CreateNode(firstKey, 30303);
        Node secondNode = CreateNode(secondKey, 30304);
        Node thirdNode = CreateNode(thirdKey, 30305);
        DiscoveredNodeStore store = new(maxRetainedNodes: 2);

        store.AddOrUpdate(firstNode, "discv4", isActive: true);
        store.AddOrUpdate(secondNode, "discv5", isActive: deactivateAfterAdd);
        if (deactivateAfterAdd)
        {
            store.Remove(secondNode, "discv5");
        }

        store.AddOrUpdate(thirdNode, "discv4", isActive: true);
        DiscoverySnapshot snapshot = store.CreateSnapshot();
        NodeDto[] retainedNodes = store.GetAllNodes();
        NodeDto[] activeRetainedNodes = store.GetActiveNodes();
        string[] retainedNodeIds = Array.ConvertAll(retainedNodes, static node => node.NodeId);
        string[] activeRetainedNodeIds = Array.ConvertAll(activeRetainedNodes, static node => node.NodeId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retainedNodes, Has.Length.EqualTo(2));
            Assert.That(retainedNodeIds, Does.Contain(firstNode.Id.ToString(false)));
            Assert.That(retainedNodeIds, Does.Not.Contain(secondNode.Id.ToString(false)));
            Assert.That(retainedNodeIds, Does.Contain(thirdNode.Id.ToString(false)));
            Assert.That(activeRetainedNodeIds, Does.Contain(firstNode.Id.ToString(false)));
            Assert.That(activeRetainedNodeIds, Does.Contain(thirdNode.Id.ToString(false)));
            Assert.That(snapshot.AllCount, Is.EqualTo(2));
            Assert.That(snapshot.ActiveCount, Is.EqualTo(2));
            Assert.That(snapshot.AllDiscv4Count, Is.EqualTo(2));
            Assert.That(snapshot.ActiveDiscv4Count, Is.EqualTo(2));
            Assert.That(snapshot.AllDiscv5Count, Is.Zero);
            Assert.That(snapshot.ActiveDiscv5Count, Is.Zero);
        }
    }

    [Test]
    public void Retention_limit_keeps_configured_bootnodes()
    {
        using PrivateKeyGenerator generator = new();
        using PrivateKey configuredKey = generator.Generate();
        using PrivateKey firstKey = generator.Generate();
        using PrivateKey secondKey = generator.Generate();
        NetworkNode configuredNetworkNode = new(configuredKey.PublicKey, "127.0.0.1", 30303);
        Node configuredNode = new(configuredNetworkNode);
        Node firstNode = CreateNode(firstKey, 30304);
        Node secondNode = CreateNode(secondKey, 30305);
        DiscoveredNodeStore store = new(maxRetainedNodes: 2);

        store.AddConfiguredBootnodes([configuredNetworkNode]);
        store.AddOrUpdate(firstNode, "discv4", isActive: false);
        store.AddOrUpdate(secondNode, "discv5", isActive: false);
        DiscoverySnapshot snapshot = store.CreateSnapshot();
        NodeDto[] retainedNodes = store.GetAllNodes(limit: 2);
        string[] retainedNodeIds = new string[retainedNodes.Length];
        for (int i = 0; i < retainedNodes.Length; i++)
        {
            retainedNodeIds[i] = retainedNodes[i].NodeId;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retainedNodeIds, Does.Contain(configuredNode.Id.ToString(false)));
            Assert.That(retainedNodeIds, Does.Not.Contain(firstNode.Id.ToString(false)));
            Assert.That(retainedNodeIds, Does.Contain(secondNode.Id.ToString(false)));
            Assert.That(snapshot.AllCount, Is.EqualTo(2));
            Assert.That(snapshot.AllConfiguredCount, Is.EqualTo(1));
            Assert.That(store.RetentionOrderCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void Repeated_observations_keep_retention_order_bounded()
    {
        using PrivateKeyGenerator generator = new();
        using PrivateKey privateKey = generator.Generate();
        Node node = CreateNode(privateKey, 30303);
        DiscoveredNodeStore store = new();

        for (int i = 0; i < 10_000; i++)
        {
            store.AddOrUpdate(node, "discv4", isActive: true);
        }

        Assert.That(store.RetentionOrderCount, Is.EqualTo(1));
    }

    [Test]
    public void Retained_node_does_not_keep_discovery_graph_alive()
    {
        DiscoveredNodeStore store = new();
        (WeakReference<Node> nodeReference, WeakReference<NodeRecord> enrReference, string enr) = AddNodeWithEnr(store);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        NodeDto retainedNode = store.GetAllNodes().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodeReference.TryGetTarget(out _), Is.False);
            Assert.That(enrReference.TryGetTarget(out _), Is.False);
            Assert.That(retainedNode.Enr, Is.EqualTo(enr));
        }

        GC.KeepAlive(store);
    }

    [TestCase(1ul)]
    [TestCase(2ul)]
    public void Lower_or_equal_enr_sequence_does_not_replace_retained_enr(ulong candidateSequence)
    {
        using PrivateKeyGenerator generator = new();
        using PrivateKey privateKey = generator.Generate();
        Node original = CreateNodeWithEnr(privateKey, sequence: 2, udpPort: 30303);
        Node candidate = CreateNodeWithEnr(privateKey, candidateSequence, udpPort: 30304);
        DiscoveredNodeStore store = new();

        store.AddOrUpdate(original, "discv5", isActive: true);
        store.AddOrUpdate(candidate, "discv5", isActive: true);

        NodeDto retainedNode = store.GetAllNodes().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(candidate.Enr!.ToString(), Is.Not.EqualTo(original.Enr!.ToString()));
            Assert.That(retainedNode.Enr, Is.EqualTo(original.Enr.ToString()));
        }
    }

    [Test]
    public void Unsigned_enr_is_not_retained()
    {
        using PrivateKeyGenerator generator = new();
        using PrivateKey privateKey = generator.Generate();
        Node node = new(privateKey.PublicKey, "127.0.0.1", 30303)
        {
            Enr = new NodeRecord()
        };
        DiscoveredNodeStore store = new();

        store.AddOrUpdate(node, "discv5", isActive: true);

        Assert.That(store.GetAllNodes().Single().Enr, Is.Null);
    }

    [Test]
    public void Removing_one_protocol_keeps_node_active_on_the_other_protocol()
    {
        using PrivateKeyGenerator generator = new();
        using PrivateKey privateKey = generator.Generate();
        Node node = CreateNode(privateKey, 30303);
        DiscoveredNodeStore store = new();

        store.AddOrUpdate(node, "discv4", isActive: true);
        store.AddOrUpdate(node, "discv5", isActive: true);
        store.Remove(node, "discv4");
        DiscoverySnapshot discv4Removed = store.CreateSnapshot();
        NodeDto[] activeNodes = store.GetActiveNodes();
        Assert.That(activeNodes, Has.Length.EqualTo(1));
        NodeDto retainedNode = activeNodes[0];
        store.Remove(node, "discv5");
        DiscoverySnapshot discv5Removed = store.CreateSnapshot();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(discv4Removed.ActiveCount, Is.EqualTo(1));
            Assert.That(discv4Removed.ActiveBothCount, Is.EqualTo(1));
            Assert.That(retainedNode.Protocol, Is.EqualTo("both"));
            Assert.That(retainedNode.Active, Is.True);
            Assert.That(discv5Removed.ActiveCount, Is.Zero);
        }
    }

    [Test]
    public void Node_queries_apply_stable_pagination()
    {
        using PrivateKeyGenerator generator = new();
        using PrivateKey firstKey = generator.Generate();
        using PrivateKey secondKey = generator.Generate();
        using PrivateKey thirdKey = generator.Generate();
        DiscoveredNodeStore store = new();
        store.AddOrUpdate(CreateNode(firstKey, 30303), "discv4", isActive: true);
        store.AddOrUpdate(CreateNode(secondKey, 30304), "discv4", isActive: true);
        store.AddOrUpdate(CreateNode(thirdKey, 30305), "discv5", isActive: false);

        NodeDto[] allNodes = store.GetAllNodes(limit: 3);
        NodeDto[] secondNode = store.GetAllNodes(offset: 1, limit: 1);
        NodeDto[] secondActiveNode = store.GetActiveNodes(offset: 1, limit: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(secondNode, Has.Length.EqualTo(1));
            Assert.That(secondNode[0].IdHash, Is.EqualTo(allNodes[1].IdHash));
            Assert.That(secondActiveNode, Has.Length.EqualTo(1));
            Assert.That(secondActiveNode[0].Active, Is.True);
        }
    }

    private static Node CreateNode(PrivateKey privateKey, int port) =>
        new(privateKey.PublicKey, "127.0.0.1", port);

    private static Node CreateNodeWithEnr(PrivateKey privateKey, ulong sequence, int udpPort)
    {
        NodeRecord nodeRecord = new() { EnrSequence = sequence };
        nodeRecord.SetEntry(new SecP256k1Entry(privateKey.CompressedPublicKey));
        nodeRecord.SetEntry(new UdpEntry(udpPort));
        new NodeRecordSigner(new Ecdsa(), privateKey).Sign(nodeRecord);
        Node node = CreateNode(privateKey, udpPort);
        node.SetVerifiedEnr(nodeRecord);
        return node;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<Node>, WeakReference<NodeRecord>, string) AddNodeWithEnr(DiscoveredNodeStore store)
    {
        using PrivateKeyGenerator generator = new();
        using PrivateKey privateKey = generator.Generate();
        NodeRecord nodeRecord = new();
        nodeRecord.SetEntry(new SecP256k1Entry(privateKey.CompressedPublicKey));
        new NodeRecordSigner(new Ecdsa(), privateKey).Sign(nodeRecord);
        Node node = new(privateKey.PublicKey, "127.0.0.1", 30303);
        node.SetVerifiedEnr(nodeRecord);
        string enr = nodeRecord.ToString();
        store.AddOrUpdate(node, "discv5", isActive: true);
        return (new WeakReference<Node>(node), new WeakReference<NodeRecord>(nodeRecord), enr);
    }
}
