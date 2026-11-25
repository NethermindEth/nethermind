// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Threading;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class DiscoveryNodeAdmissionTests
{
    private readonly List<IContainer> _containers = [];

    [TearDown]
    public async Task DisposeContainers()
    {
        // Containers from CreateAdmission must outlive the assertions, so they are released here.
        for (int i = _containers.Count - 1; i >= 0; i--)
        {
            await _containers[i].DisposeAsync();
        }

        _containers.Clear();
    }

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
    public void Repeated_contact_keeps_the_subnet_reserved()
    {
        Fixture fixture = CreateAdmission();
        Node known = CreateNode(TestItem.PublicKeyA, "203.0.113.10");

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
    public void Promotion_extends_a_live_reservation_for_the_cached_node()
    {
        ManualTimeProvider clock = new();
        Fixture fixture = CreateAdmission(timeProvider: clock);
        Node cached = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        Node sameSubnet = CreateNode(TestItem.PublicKeyB, "203.0.113.20");

        Assert.That(fixture.Admission.TryAdmit(cached), Is.True);
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.That(fixture.Admission.TryAdmitPromoted(cached), Is.True);

        clock.Advance(TimeSpan.FromMinutes(2));

        using (Assert.EnterMultipleScope())
        {
            // Only the promotion's renewal keeps the window alive: six minutes after the first admission, an
            // unrefreshed entry would have expired and let another identity take the subnet.
            Assert.That(fixture.Admission.TryAdmit(sameSubnet), Is.False);
            Assert.That(fixture.Admission.TryAdmitPromoted(cached), Is.True);
        }
    }

    [Test]
    public void Promotion_rejects_a_replacement_whose_reservation_was_taken_over()
    {
        ManualTimeProvider clock = new();
        Fixture fixture = CreateAdmission(timeProvider: clock);
        Node cached = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        Node sameSubnet = CreateNode(TestItem.PublicKeyB, "203.0.113.20");

        Assert.That(fixture.Admission.TryAdmit(cached), Is.True);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.That(fixture.Admission.TryAdmit(sameSubnet), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Admission.TryAdmitPromoted(cached), Is.False);
            // The identity that holds the reservation is still promotable.
            Assert.That(fixture.Admission.TryAdmitPromoted(sameSubnet), Is.True);
        }
    }

    [Test]
    public void Promotion_admits_a_configured_exempt_replacement_without_a_reservation()
    {
        ManualTimeProvider clock = new();
        Node self = CreateNode(TestItem.PublicKeyF, "198.51.100.1");
        Node bootnode = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        Fixture fixture = CreateAdmission(
            timeProvider: clock,
            config: new KademliaConfig<Node>
            {
                CurrentNodeId = self,
                BootNodes = [bootnode],
            });
        Node sameSubnet = CreateNode(TestItem.PublicKeyB, "203.0.113.20");

        // A bootnode never creates a reservation, so the subnet is free for another identity to take.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Admission.TryAdmit(bootnode), Is.True);
            Assert.That(fixture.Admission.TryAdmit(sameSubnet), Is.True);
        }

        using (Assert.EnterMultipleScope())
        {
            // The configured identity stays exempt even though another identity now holds the subnet.
            Assert.That(fixture.Admission.TryAdmitPromoted(bootnode), Is.True);
            Assert.That(fixture.Admission.TryAdmitPromoted(sameSubnet), Is.True);
        }
    }

    [Test]
    public void Reclaims_its_live_address_reservation()
    {
        ManualTimeProvider clock = new();
        Fixture fixture = CreateAdmission(timeProvider: clock);
        Node node = CreateNode(TestItem.PublicKeyA, "203.0.113.10");

        Assert.That(fixture.Admission.TryAdmit(node), Is.True);

        clock.Advance(TimeSpan.FromMinutes(4));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "203.0.114.20")), Is.True);
            // Its address entry is still live and names the same owner, so it must not exclude the owner.
            Assert.That(fixture.Admission.TryAdmit(node), Is.True);
        }

        // The contact above renewed the window: the first reservation would have expired at five minutes, but
        // the subnet is still the node's.
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
    public void Persisted_enode_is_exempt_at_its_stored_address()
    {
        Fixture fixture = CreateAdmission(
            persisted: [new NetworkNode(TestItem.PublicKeyA, "2001:db8::1", 30303)],
            localIp: "2001:db8::1");

        using (Assert.EnterMultipleScope())
        {
            // Another identity holds the /64, so the persisted node would be filtered without its exemption.
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "2001:db8::2")), Is.True);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyA, "2001:db8::1")), Is.True);
        }
    }

    [Test]
    public void Persisted_legacy_row_with_ipv4_mapped_host_keeps_its_exemption()
    {
        Fixture fixture = CreateAdmission(
            persisted: [new NetworkNode(new Enode(TestItem.PublicKeyA, IPAddress.Parse("::ffff:203.0.113.10"), 30303))]);

        using (Assert.EnterMultipleScope())
        {
            // Another identity holds the /24, so the persisted node would be filtered without its exemption.
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "203.0.113.20")), Is.True);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyA, "203.0.113.10")), Is.True);
        }
    }

    [Test]
    public void Persisted_enr_is_not_exempt_at_its_advertised_address()
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
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "2001:db8::2")), Is.True);
            // The ENR does not prove the advertised endpoint was admitted, so it goes through the filter.
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyA, "2001:db8::1")), Is.False);
        }
    }

    [Test]
    public void Trusted_static_and_bootnode_nodes_bypass_the_filter_without_consuming_capacity()
    {
        Fixture fixture = CreateAdmission(
            staticNodes: StaticNodes([new NetworkNode(TestItem.PublicKeyB, "203.0.113.11", 30303)]),
            trustedNodes: TrustedNodes([new NetworkNode(TestItem.PublicKeyA, "203.0.113.10", 30303)]));
        Node trusted = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        Node staticNode = CreateNode(TestItem.PublicKeyB, "203.0.113.11");
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
    public void Instance_flags_do_not_exempt_a_node_its_manager_no_longer_lists()
    {
        Fixture fixture = CreateAdmission();
        // An ordinary node already holds the subnet.
        Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyE, "203.0.113.20")), Is.True);

        Node flaggedTrusted = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        flaggedTrusted.IsTrusted = true;
        Node flaggedStatic = CreateNode(TestItem.PublicKeyB, "203.0.113.11");
        flaggedStatic.IsStatic = true;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Admission.TryAdmit(flaggedTrusted), Is.False);
            Assert.That(fixture.Admission.TryAdmit(flaggedStatic), Is.False);
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
        IStaticNodesManager staticNodes = StaticNodes([new NetworkNode(TestItem.PublicKeyA, "203.0.113.10", 30303)]);
        ITrustedNodesManager trustedNodes = TrustedNodes([new NetworkNode(TestItem.PublicKeyB, "203.0.114.20", 30303)]);
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
    public void Known_node_that_became_managed_stops_renewing_its_reservation()
    {
        ManualTimeProvider clock = new();
        List<NetworkNode> staticNodesList = [];
        IStaticNodesManager staticNodes = StaticNodes(staticNodesList);
        Fixture fixture = CreateAdmission(timeProvider: clock, staticNodes: staticNodes);
        Node ordinary = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        Node sameSubnet = CreateNode(TestItem.PublicKeyB, "203.0.113.20");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Admission.TryAdmit(ordinary), Is.True);
            clock.Advance(TimeSpan.FromMinutes(4));
            // An ordinary node renews its own reservation.
            Assert.That(fixture.Admission.TryAdmit(ordinary), Is.True);
            Assert.That(fixture.Admission.TryAdmit(sameSubnet), Is.False);
        }

        // The operator makes the node static while it still holds the subnet.
        staticNodesList.Add(new NetworkNode(TestItem.PublicKeyA, "203.0.113.10", 30303));
        clock.Advance(TimeSpan.FromMinutes(4));

        // The exemption admits it, but the live check runs before the reservation is extended...
        Assert.That(fixture.Admission.TryAdmit(ordinary), Is.True);

        clock.Advance(TimeSpan.FromMinutes(2));

        // ...so the subnet is free once the last renewal expires instead of being held indefinitely.
        Assert.That(fixture.Admission.TryAdmit(sameSubnet), Is.True);
    }

    [Test]
    public void Promoted_node_that_became_managed_stops_renewing_its_reservation()
    {
        ManualTimeProvider clock = new();
        List<NetworkNode> staticNodesList = [];
        IStaticNodesManager staticNodes = StaticNodes(staticNodesList);
        Fixture fixture = CreateAdmission(timeProvider: clock, staticNodes: staticNodes);
        Node cached = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        Node sameSubnet = CreateNode(TestItem.PublicKeyB, "203.0.113.20");

        Assert.That(fixture.Admission.TryAdmit(cached), Is.True);
        clock.Advance(TimeSpan.FromMinutes(4));

        using (Assert.EnterMultipleScope())
        {
            // An ordinary node's promotion extends its own reservation.
            Assert.That(fixture.Admission.TryAdmitPromoted(cached), Is.True);
            Assert.That(fixture.Admission.TryAdmit(sameSubnet), Is.False);
        }

        // The operator makes the node static while it still holds the subnet.
        staticNodesList.Add(new NetworkNode(TestItem.PublicKeyA, "203.0.113.10", 30303));
        clock.Advance(TimeSpan.FromMinutes(4));

        // The exemption admits the promotion without extending the reservation...
        Assert.That(fixture.Admission.TryAdmitPromoted(cached), Is.True);

        clock.Advance(TimeSpan.FromMinutes(2));

        // ...so the subnet is free once the last renewal expires.
        Assert.That(fixture.Admission.TryAdmit(sameSubnet), Is.True);
    }

    [Test]
    public void First_contact_probes_each_managed_set_once()
    {
        int staticProbes = 0;
        int trustedProbes = 0;
        IStaticNodesManager staticNodes = Substitute.For<IStaticNodesManager>();
        staticNodes.ContainsNode(Arg.Any<PublicKey>()).Returns(_ =>
        {
            staticProbes++;
            return false;
        });
        ITrustedNodesManager trustedNodes = Substitute.For<ITrustedNodesManager>();
        trustedNodes.ContainsNode(Arg.Any<PublicKey>()).Returns(_ =>
        {
            trustedProbes++;
            return false;
        });
        Fixture fixture = CreateAdmission(staticNodes: staticNodes, trustedNodes: trustedNodes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyA, "203.0.113.10")), Is.True);
            Assert.That(staticProbes, Is.EqualTo(1), "admission probes each managed set once");
            Assert.That(trustedProbes, Is.EqualTo(1), "admission probes each managed set once");
        }
    }

    [Test]
    public void Managed_node_that_lost_its_reservation_is_still_admitted()
    {
        IStaticNodesManager staticNodes = StaticNodes([new NetworkNode(TestItem.PublicKeyA, "203.0.113.10", 30303)]);
        Fixture fixture = CreateAdmission(staticNodes: staticNodes);
        Node managed = CreateNode(TestItem.PublicKeyA, "203.0.113.10");

        using (Assert.EnterMultipleScope())
        {
            // Another identity already holds the subnet...
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "203.0.113.20")), Is.True);
            // ...so the managed node's refresh fails and only its exemption can admit it.
            Assert.That(fixture.Admission.TryAdmit(managed), Is.True);
        }
    }

    [Test]
    public void Static_node_added_after_construction_is_exempt_on_first_contact()
    {
        List<NetworkNode> staticNodesList = [];
        IStaticNodesManager staticNodes = StaticNodes(staticNodesList);
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
        IStaticNodesManager staticNodes = StaticNodes(staticNodesList);
        ITrustedNodesManager trustedNodes = TrustedNodes(trustedNodesList);
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
            // A still-flagged instance carries no entitlement once the operator removed it...
            Assert.That(fixture.Admission.TryAdmit(flaggedStatic), Is.False);
            Assert.That(fixture.Admission.TryAdmit(flaggedTrusted), Is.False);
            // ...and an unflagged rebuild is rejected just the same.
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyA, "203.0.113.10")), Is.False);
            Assert.That(fixture.Admission.TryAdmit(CreateNode(TestItem.PublicKeyB, "203.0.114.20")), Is.False);
        }
    }

    [Test]
    public void Promotion_drops_a_stale_static_flag_after_the_node_leaves_its_manager()
    {
        List<NetworkNode> staticNodesList = [new(TestItem.PublicKeyA, "203.0.113.10", 30303)];
        IStaticNodesManager staticNodes = StaticNodes(staticNodesList);
        Fixture fixture = CreateAdmission(staticNodes: staticNodes);
        Node cached = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        cached.IsStatic = true;
        Node sameSubnet = CreateNode(TestItem.PublicKeyB, "203.0.113.20");

        Assert.That(fixture.Admission.TryAdmit(cached), Is.True);

        // The operator removes the node while it waits in a replacement cache, and another identity takes the subnet.
        staticNodesList.Clear();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Admission.TryAdmit(sameSubnet), Is.True);
            // The cached instance still carries the flag, but the removal is observed on the next admission.
            Assert.That(fixture.Admission.TryAdmitPromoted(cached), Is.False);
            Assert.That(fixture.Admission.TryAdmitPromoted(sameSubnet), Is.True);
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

    [Test]
    public void Concurrent_promotion_and_admission_of_the_same_node_never_reject_it()
    {
        ManualTimeProvider clock = new();
        Fixture fixture = CreateAdmission(timeProvider: clock);
        Node node = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        const int Rounds = 20;
        const int Racers = 17;

        // Dedicated threads start together; pool threads would be injected one at a time and start racing after
        // the first racer already finished. The driver joins both phases so it cannot advance the clock while a
        // racer is still in the previous round, and the workers are reused instead of restarted per round.
        using Barrier roundStart = new(Racers + 1);
        using Barrier roundDone = new(Racers + 1);
        bool[] results = new bool[Racers];
        Thread[] threads = new Thread[Racers];
        for (int index = 0; index < threads.Length; index++)
        {
            int racer = index;
            threads[index] = new Thread(() =>
            {
                for (int i = 0; i < Rounds; i++)
                {
                    if (!roundStart.SignalAndWait(TimeSpan.FromSeconds(30))) return;

                    results[racer] = racer == 0
                        ? fixture.Admission.TryAdmit(node)
                        : fixture.Admission.TryAdmitPromoted(node);

                    if (!roundDone.SignalAndWait(TimeSpan.FromSeconds(30))) return;
                }
            })
            {
                IsBackground = true,
            };

            threads[index].Start();
        }

        for (int i = 0; i < Rounds; i++)
        {
            // Every path expires the reservation, so the racers all fail their refresh check and only one of
            // them can acquire it; the others must recognize the identity that just reacquired it.
            clock.Advance(TimeSpan.FromMinutes(5));

            bool raced = roundStart.SignalAndWait(TimeSpan.FromSeconds(30))
                         && roundDone.SignalAndWait(TimeSpan.FromSeconds(30));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(raced, Is.True, $"iteration {i} barrier");
                Assert.That(results, Is.All.True, $"iteration {i}");
            }
        }

        foreach (Thread thread in threads) thread.Join();
    }

    private Fixture CreateAdmission(
        bool enabled = true,
        bool subnetBucketing = true,
        KademliaConfig<Node>? config = null,
        NetworkNode[]? persisted = null,
        string currentIp = "198.51.100.1",
        string? localIp = null,
        TimeProvider? timeProvider = null,
        IStaticNodesManager? staticNodes = null,
        ITrustedNodesManager? trustedNodes = null,
        IEnumerable<NetworkNode>? configuredStaticNodes = null)
    {
        IContainer container = DiscoveryAdmissionTestContainer.Create(
            enabled: enabled,
            subnetBucketing: subnetBucketing,
            currentIp: currentIp,
            localIp: localIp,
            timeProvider: timeProvider,
            persisted: persisted,
            kademliaConfig: config,
            routingTable: Substitute.For<IRoutingTable<Node, ValueHash256>>(),
            storage: Substitute.For<INetworkStorage>(),
            staticNodes: staticNodes,
            trustedNodes: trustedNodes,
            configuredStaticNodes: configuredStaticNodes);
        _containers.Add(container);
        return new Fixture(container.Resolve<DiscoveryNodeAdmission>());
    }

    private static Node CreateNode(PublicKey id, string host) => new(id, host, 30303, 30303);

    // The admission resolves managed exemptions by identity through ContainsNode; Nodes keeps the rest of the
    // resolved graph working. Both track the same list, so additions and removals are observed live.
    private static IStaticNodesManager StaticNodes(List<NetworkNode> nodes)
    {
        IStaticNodesManager manager = Substitute.For<IStaticNodesManager>();
        manager.Nodes.Returns(_ => nodes);
        manager.ContainsNode(Arg.Any<PublicKey>()).Returns(call => nodes.Any(n => n.NodeId.Equals(call.Arg<PublicKey>())));
        return manager;
    }

    private static ITrustedNodesManager TrustedNodes(List<NetworkNode> nodes)
    {
        ITrustedNodesManager manager = Substitute.For<ITrustedNodesManager>();
        manager.Nodes.Returns(_ => nodes);
        manager.ContainsNode(Arg.Any<PublicKey>()).Returns(call => nodes.Any(n => n.NodeId.Equals(call.Arg<PublicKey>())));
        return manager;
    }

    private sealed record Fixture(DiscoveryNodeAdmission Admission);
}
