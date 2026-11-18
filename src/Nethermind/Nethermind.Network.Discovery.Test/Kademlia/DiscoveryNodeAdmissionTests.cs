// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Threading;
using Nethermind.Kademlia;
using Nethermind.Network;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class DiscoveryNodeAdmissionTests
{
    [Test]
    public void Rejects_second_node_from_same_subnet_or_exact_ip(
        [Values("203.0.113.10", "203.0.113.20")] string secondNodeIp)
    {
        Fixture fixture = CreateAdmission();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyA, "203.0.113.10")), Is.True);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, secondNodeIp)), Is.False);
        }
    }

    [Test]
    public void Accepts_nodes_from_different_subnets()
    {
        Fixture fixture = CreateAdmission();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyA, "203.0.113.10")), Is.True);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "203.0.114.20")), Is.True);
        }
    }

    [Test]
    public void When_subnet_bucketing_is_disabled_uses_exact_ip()
    {
        Fixture fixture = CreateAdmission(subnetBucketing: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyA, "203.0.113.10")), Is.True);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "203.0.113.20")), Is.True);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyC, "203.0.113.10")), Is.False);
        }
    }

    [Test]
    public void TryAdmit_is_idempotent_for_the_same_node()
    {
        Fixture fixture = CreateAdmission();
        Node node = CreateNode(TestItem.PublicKeyA, "203.0.113.10");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Admission.TryAdmit(node), Is.True);
            Assert.That(fixture.Admission.TryAdmit(node), Is.True);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "203.0.113.20")), Is.False);
        }
    }

    [Test]
    public void Known_node_touches_its_address_so_a_same_subnet_node_is_rejected()
    {
        Fixture fixture = CreateAdmission();
        Node known = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        fixture.RoutingTable.TryGet(Arg.Is<ValueHash256>(hash => hash == known.Id.Hash), out _)
            .Returns(ci =>
            {
                ci[1] = known;
                return true;
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Admission.TryAdmit(known), Is.True);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "203.0.113.20")), Is.False);
        }
    }

    [Test]
    public void Known_node_moving_to_another_nodes_subnet_is_rejected()
    {
        Fixture fixture = CreateAdmission();
        Node first = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        Node second = CreateNode(TestItem.PublicKeyB, "203.0.114.10");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Admission.TryAdmit(first), Is.True);
            Assert.That(fixture.Admission.TryAdmit(second), Is.True);
            // Identity is not an address claim: the second node may not move into the first node's subnet.
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "203.0.113.20")), Is.False);
            // It can still refresh its own subnet, and so can the first node.
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "203.0.114.20")), Is.True);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyA, "203.0.113.20")), Is.True);
        }
    }

    [Test]
    public void Known_identities_cannot_concentrate_into_one_subnet()
    {
        Fixture fixture = CreateAdmission();
        Node first = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        Node second = CreateNode(TestItem.PublicKeyB, "203.0.114.10");
        Node third = CreateNode(TestItem.PublicKeyC, "203.0.115.10");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Admission.TryAdmit(first), Is.True);
            Assert.That(fixture.Admission.TryAdmit(second), Is.True);
            Assert.That(fixture.Admission.TryAdmit(third), Is.True);
            // Only the first identity can reserve the target subnet; the per-subnet limit still holds.
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyA, "203.0.116.10")), Is.True);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "203.0.116.20")), Is.False);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyC, "203.0.116.30")), Is.False);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyE, "203.0.116.40")), Is.False);
        }
    }

    [Test]
    public void Known_node_moving_to_a_free_subnet_reserves_it()
    {
        Fixture fixture = CreateAdmission();
        Node node = CreateNode(TestItem.PublicKeyA, "203.0.113.10");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Admission.TryAdmit(node), Is.True);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyA, "203.0.114.10")), Is.True);
            // The move reserved the new subnet against other nodes.
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "203.0.114.20")), Is.False);
        }
    }

    [Test]
    public void Same_subnet_node_is_admitted_after_the_holder_goes_silent_for_the_window()
    {
        ManualTimeProvider clock = new();
        Fixture fixture = CreateAdmission(timeProvider: clock);
        Node holder = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        Node sameSubnet = CreateNode(TestItem.PublicKeyB, "203.0.113.20");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Admission.TryAdmit(holder), Is.True);
            Assert.That(fixture.Admission.TryAdmit(sameSubnet), Is.False);
        }

        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.That(fixture.Admission.TryAdmit(sameSubnet), Is.True);
    }

    [Test]
    public void Returning_identity_is_rejected_after_another_identity_takes_its_reservation()
    {
        ManualTimeProvider clock = new();
        Fixture fixture = CreateAdmission(timeProvider: clock);
        Node first = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        Node second = CreateNode(TestItem.PublicKeyB, "203.0.113.20");

        Assert.That(fixture.Admission.TryAdmit(first), Is.True);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.That(fixture.Admission.TryAdmit(second), Is.True);

        using (Assert.EnterMultipleScope())
        {
            // The first identity's window expired, but the reservation now belongs to the second identity.
            Assert.That(fixture.Admission.TryAdmit(first), Is.False);
            // The holder can still extend its own reservation.
            Assert.That(fixture.Admission.TryAdmit(second), Is.True);
        }

        clock.Advance(TimeSpan.FromMinutes(5));

        // Once the new holder is silent for a full window the subnet is free again.
        Assert.That(fixture.Admission.TryAdmit(first), Is.True);
    }

    [Test]
    public void Reclaims_its_live_address_reservation_after_its_identity_is_evicted()
    {
        ManualTimeProvider clock = new();
        Fixture fixture = CreateAdmission(timeProvider: clock, admittedCapacity: 1);
        Node node = CreateNode(TestItem.PublicKeyA, "203.0.113.10");

        Assert.That(fixture.Admission.TryAdmit(node), Is.True);

        clock.Advance(TimeSpan.FromMinutes(4));
        // A newer identity evicts the first one from the admitted cache, and the routing table does not know
        // it, so the returning node takes the unknown-node path.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "203.0.114.20")), Is.True);
            // Its address entry is still live and names the same owner, so it must not exclude the owner.
            Assert.That(fixture.Admission.TryAdmit(node), Is.True);
        }

        // The reclaim renewed the window at the contact above: the first reservation would have expired at
        // five minutes, but the subnet is still the node's.
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyC, "203.0.113.99")), Is.False);
    }

    [Test]
    public void Exempt_nodes_bypass_the_filter_without_consuming_capacity()
    {
        Fixture fixture = CreateAdmission(
            persisted: [new NetworkNode(TestItem.PublicKeyA, "203.0.113.10", 30303)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyA, "203.0.113.10")), Is.True);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "203.0.113.20")), Is.True);
        }
    }

    [Test]
    public void Persisted_nodes_from_the_same_subnet_keep_their_exemption()
    {
        Fixture fixture = CreateAdmission(persisted:
        [
            new NetworkNode(TestItem.PublicKeyA, "203.0.113.10", 30303),
            new NetworkNode(TestItem.PublicKeyB, "203.0.113.20", 30303),
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyA, "203.0.113.10")), Is.True);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "203.0.113.20")), Is.True);
        }
    }

    [Test]
    public void Persisted_dual_stack_node_is_exempt_at_its_restored_ipv6_address()
    {
        NodeRecord record = TestEnrBuilder.BuildSigned(
            TestItem.PrivateKeyA,
            IPAddress.Parse("192.0.2.1"),
            tcpPort: 30303,
            udpPort: 30304,
            configureExtras: enr =>
            {
                enr.SetEntry(new Ip6Entry(IPAddress.Parse("2001:db8::1")));
                enr.SetEntry(new Tcp6Entry(30306));
                enr.SetEntry(new Udp6Entry(30305));
            });
        Fixture fixture = CreateAdmission(
            persisted: [new NetworkNode(record.ToString())],
            localIp: "2001:db8::1");

        using (Assert.EnterMultipleScope())
        {
            // Another identity holds the /64, so the persisted node would be filtered without its exemption.
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "2001:db8::2")), Is.True);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyA, "2001:db8::1")), Is.True);
        }
    }

    [Test]
    public void Trusted_static_and_bootnode_nodes_bypass_the_filter_without_consuming_capacity()
    {
        Fixture fixture = CreateAdmission();
        Node trusted = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        trusted.IsTrusted = true;
        Node staticNode = CreateNode(TestItem.PublicKeyB, "203.0.113.11");
        staticNode.IsStatic = true;
        Node bootnode = CreateNode(TestItem.PublicKeyC, "203.0.113.12");
        bootnode.IsBootnode = true;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Admission.TryAdmit(trusted), Is.True);
            Assert.That(fixture.Admission.TryAdmit(staticNode), Is.True);
            Assert.That(fixture.Admission.TryAdmit(bootnode), Is.True);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyE, "203.0.113.13")), Is.True);
        }
    }

    [Test]
    public void Self_and_configured_bootnode_bypass_the_filter_without_consuming_capacity()
    {
        Node self = CreateNode(TestItem.PublicKeyF, "198.51.100.2");
        Node bootnode = CreateNode(TestItem.PublicKeyE, "203.0.113.15");
        Fixture fixture = CreateAdmission(config: new KademliaConfig<Node>
        {
            CurrentNodeId = self,
            BootNodes = [bootnode],
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Admission.TryAdmit(self), Is.True);
            Assert.That(fixture.Admission.TryAdmit(bootnode), Is.True);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyA, "203.0.113.10")), Is.True);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "203.0.113.20")), Is.False);
        }
    }

    [Test]
    public void Flag_exemption_applies_only_to_the_current_call()
    {
        Fixture fixture = CreateAdmission();
        // An ordinary node already holds the subnet.
        Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyE, "203.0.113.20")), Is.True);

        Node flagged = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        flagged.IsTrusted = true;

        using (Assert.EnterMultipleScope())
        {
            // The flag admits the flagged instance without reserving the subnet...
            Assert.That(fixture.Admission.TryAdmit(flagged), Is.True);
            // ...but a packet rebuild without the flag inherits no lasting identity entitlement.
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyA, "203.0.113.10")), Is.False);
        }
    }

    [Test]
    public void Configured_static_peers_are_exempt_by_identity()
    {
        Fixture fixture = CreateAdmission(configuredStaticNodes:
            [new NetworkNode(TestItem.PublicKeyA, "203.0.113.10", 30303)]);

        using (Assert.EnterMultipleScope())
        {
            // A packet rebuilds the node without its flags.
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyA, "203.0.113.10")), Is.True);
            // The configured static peer did not reserve its subnet.
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "203.0.113.99")), Is.True);
        }
    }

    [Test]
    public void Static_and_trusted_nodes_are_exempt_on_first_contact_by_identity()
    {
        IStaticNodesManager staticNodes = Substitute.For<IStaticNodesManager>();
        staticNodes.Nodes.Returns([new NetworkNode(TestItem.PublicKeyA, "203.0.113.10", 30303)]);
        ITrustedNodesManager trustedNodes = Substitute.For<ITrustedNodesManager>();
        trustedNodes.Nodes.Returns([new NetworkNode(TestItem.PublicKeyB, "203.0.114.20", 30303)]);
        Fixture fixture = CreateAdmission(staticNodes: staticNodes, trustedNodes: trustedNodes);

        using (Assert.EnterMultipleScope())
        {
            // First contact arrives as a fresh, unflagged node built from the packet.
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyA, "203.0.113.10")), Is.True);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "203.0.114.20")), Is.True);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyC, "203.0.115.30")), Is.True);
            // The exempt nodes did not reserve their subnets.
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyE, "203.0.113.99")), Is.True);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyF, "203.0.114.99")), Is.True);
        }
    }

    [Test]
    public void Static_node_added_after_construction_is_exempt_on_first_contact()
    {
        List<NetworkNode> staticNodesList = [];
        IStaticNodesManager staticNodes = Substitute.For<IStaticNodesManager>();
        staticNodes.Nodes.Returns(_ => staticNodesList);
        Fixture fixture = CreateAdmission(staticNodes: staticNodes);
        staticNodesList.Add(new NetworkNode(TestItem.PublicKeyA, "203.0.113.10", 30303));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyA, "203.0.113.10")), Is.True);
            // The newly added static node did not reserve its subnet.
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "203.0.113.99")), Is.True);
        }
    }

    [Test]
    public void Removing_a_managed_node_revokes_its_exemption()
    {
        List<NetworkNode> staticNodesList = [new(TestItem.PublicKeyA, "203.0.113.10", 30303)];
        List<NetworkNode> trustedNodesList = [new(TestItem.PublicKeyB, "203.0.114.20", 30303)];
        IStaticNodesManager staticNodes = Substitute.For<IStaticNodesManager>();
        staticNodes.Nodes.Returns(_ => staticNodesList);
        ITrustedNodesManager trustedNodes = Substitute.For<ITrustedNodesManager>();
        trustedNodes.Nodes.Returns(_ => trustedNodesList);
        Fixture fixture = CreateAdmission(staticNodes: staticNodes, trustedNodes: trustedNodes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyA, "203.0.113.10")), Is.True);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "203.0.114.20")), Is.True);
            // The exempt nodes left their subnets free for an ordinary node.
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyE, "203.0.113.99")), Is.True);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyF, "203.0.114.99")), Is.True);
        }

        staticNodesList.Clear();
        trustedNodesList.Clear();

        Node flaggedStatic = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        flaggedStatic.IsStatic = true;
        Node flaggedTrusted = CreateNode(TestItem.PublicKeyB, "203.0.114.20");
        flaggedTrusted.IsTrusted = true;

        using (Assert.EnterMultipleScope())
        {
            // A still-flagged instance is admitted for its own call...
            Assert.That(fixture.Admission.TryAdmit(flaggedStatic), Is.True);
            Assert.That(fixture.Admission.TryAdmit(flaggedTrusted), Is.True);
            // ...but it does not restore the identity's exemption.
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyA, "203.0.113.10")), Is.False);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "203.0.114.20")), Is.False);
        }
    }

    [Test]
    public void Disabled_admission_accepts_everything()
    {
        Fixture fixture = CreateAdmission(enabled: false);
        Node first = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        Node second = CreateNode(TestItem.PublicKeyB, "203.0.113.20");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Admission.TryAdmit(first), Is.True);
            Assert.That(fixture.Admission.TryAdmit(second), Is.True);
        }
    }

    [Test]
    public async Task Concurrent_TryAdmit_for_the_same_node_is_idempotent()
    {
        Fixture fixture = CreateAdmission();
        Node node = CreateNode(TestItem.PublicKeyA, "203.0.113.10");

        bool[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(() => fixture.Admission.TryAdmit(node))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Is.All.True);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "203.0.113.20")), Is.False);
        }
    }

    private static Fixture CreateAdmission(
        bool enabled = true,
        bool subnetBucketing = true,
        KademliaConfig<Node>? config = null,
        NetworkNode[]? persisted = null,
        string currentIp = "198.51.100.1",
        string? localIp = null,
        TimeProvider? timeProvider = null,
        IStaticNodesManager? staticNodes = null,
        ITrustedNodesManager? trustedNodes = null,
        IEnumerable<NetworkNode>? configuredStaticNodes = null,
        int admittedCapacity = 100)
    {
        IRoutingTable<Node, ValueHash256> routingTable = Substitute.For<IRoutingTable<Node, ValueHash256>>();
        INetworkStorage storage = Substitute.For<INetworkStorage>();
        IContainer container = DiscoveryAdmissionTestContainer.Create(
            enabled: enabled,
            subnetBucketing: subnetBucketing,
            currentIp: currentIp,
            localIp: localIp,
            timeProvider: timeProvider,
            persisted: persisted,
            kademliaConfig: config,
            routingTable: routingTable,
            storage: storage,
            staticNodes: staticNodes,
            trustedNodes: trustedNodes,
            configuredStaticNodes: configuredStaticNodes,
            admittedCapacity: admittedCapacity);
        return new Fixture(container.Resolve<DiscoveryNodeAdmission>(), routingTable);
    }

    private static Node CreateNode(PublicKey id, string host) => new(id, host, 30303, 30303);

    private sealed record Fixture(
        DiscoveryNodeAdmission Admission,
        IRoutingTable<Node, ValueHash256> RoutingTable);
}
