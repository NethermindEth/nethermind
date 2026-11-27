// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

#pragma warning disable NUnit2045 // Assertions are interleaved with limiter mutations; a failure must stop the sequence.

public class DiscoveryNodeLimiterTests
{
    private NetworkConfig _networkConfig = null!;
    private IDiscoveryNodeExemptions _exemptions = null!;
    private Node _currentNode = null!;
    private Node _bootNode = null!;

    [SetUp]
    public void Setup()
    {
        _networkConfig = new NetworkConfig();
        _exemptions = Substitute.For<IDiscoveryNodeExemptions>();
        _exemptions.IsExempt(Arg.Any<PublicKey>()).Returns(false);

        _currentNode = CreateNode(TestItem.PublicKeyA, "192.0.2.1");
        _bootNode = CreateNode(TestItem.PublicKeyB, "198.51.100.1");
    }

    [Test]
    public void Limits_match_geth_table_and_bucket_limits()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(DiscoveryNodeLimiter.TableLimit, Is.EqualTo(10));
            Assert.That(DiscoveryNodeLimiter.BucketLimit, Is.EqualTo(2));
        }
    }

    [Test]
    public void TryAdmit_refuses_one_past_the_table_limit()
    {
        DiscoveryNodeLimiter limiter = CreateLimiter();
        AdmitNodes(limiter);

        Node eleventh = CreateNode(TestItem.PrivateKeys[10].PublicKey, "192.0.2.200");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(limiter.TryAdmit(eleventh, []), Is.False);
            Assert.That(limiter.WouldAdmit(eleventh), Is.False);
            // A different subnet still has room, and probing it twice reserves nothing.
            Assert.That(limiter.WouldAdmit(CreateNode(TestItem.PrivateKeys[11].PublicKey, "203.0.113.1")), Is.True);
            Assert.That(limiter.WouldAdmit(CreateNode(TestItem.PrivateKeys[12].PublicKey, "203.0.113.2")), Is.True);
        }
    }

    [Test]
    public void TryAdmit_refuses_one_past_the_bucket_limit_while_the_table_has_room()
    {
        DiscoveryNodeLimiter limiter = CreateLimiter();
        List<Node> bucket = [];

        Node first = CreateNode(TestItem.PrivateKeys[0].PublicKey, "192.0.2.1");
        Node second = CreateNode(TestItem.PrivateKeys[1].PublicKey, "192.0.2.2");
        Node third = CreateNode(TestItem.PrivateKeys[2].PublicKey, "192.0.2.3");
        Assert.That(limiter.TryAdmit(first, bucket.ToArray()), Is.True);
        bucket.Add(first);
        Assert.That(limiter.TryAdmit(second, bucket.ToArray()), Is.True);
        bucket.Add(second);

        Assert.That(limiter.TryAdmit(third, bucket.ToArray()), Is.False);
        // The table-wide probe has no bucket to scan, so it only reports the table slot.
        Assert.That(limiter.WouldAdmit(third), Is.True);

        // The same subnet node is still admissible in another bucket because the table has room.
        Assert.That(limiter.TryAdmit(third, []), Is.True);
    }

    [TestCase("192.0.2.1", "192.0.2.200", true)]
    [TestCase("192.0.2.1", "192.0.3.1", false)]
    [TestCase("2001:db8:1:2::1", "2001:db8:1:2::ffff", true)]
    [TestCase("2001:db8:1:2::1", "2001:db8:1:3::1", false)]
    public void TryAdmit_keys_public_addresses_by_24_ipv6_64(
        string firstAddress,
        string candidateAddress,
        bool sameKey)
    {
        DiscoveryNodeLimiter limiter = CreateLimiter();
        Node first = CreateNode(TestItem.PrivateKeys[0].PublicKey, firstAddress);
        Node second = CreateNode(TestItem.PrivateKeys[1].PublicKey, firstAddress);
        Node candidate = CreateNode(TestItem.PrivateKeys[2].PublicKey, candidateAddress);

        List<Node> bucket = [];
        Assert.That(limiter.TryAdmit(first, bucket.ToArray()), Is.True);
        bucket.Add(first);
        Assert.That(limiter.TryAdmit(second, bucket.ToArray()), Is.True);
        bucket.Add(second);

        // The bucket is saturated for the first key, so only a different key can still enter it.
        Assert.That(limiter.TryAdmit(candidate, bucket.ToArray()), Is.EqualTo(!sameKey));
    }

    [Test]
    public void SameSubnet_filter_disabled_keys_every_public_address_exactly()
    {
        _networkConfig.FilterDiscoveryNodesBySameSubnet = false;
        DiscoveryNodeLimiter limiter = CreateLimiter();
        Node first = CreateNode(TestItem.PrivateKeys[0].PublicKey, "192.0.2.1");
        Node second = CreateNode(TestItem.PrivateKeys[1].PublicKey, "192.0.2.1");
        Node sameSubnetDifferentAddress = CreateNode(TestItem.PrivateKeys[2].PublicKey, "192.0.2.200");

        List<Node> bucket = [];
        Assert.That(limiter.TryAdmit(first, bucket.ToArray()), Is.True);
        bucket.Add(first);
        Assert.That(limiter.TryAdmit(second, bucket.ToArray()), Is.True);
        bucket.Add(second);

        Assert.That(limiter.TryAdmit(sameSubnetDifferentAddress, bucket.ToArray()), Is.True);
    }

    [Test]
    public void Lan_addresses_bypass_table_and_bucket_limits_while_testnets_stay_capped()
    {
        DiscoveryNodeLimiter limiter = CreateLimiter();
        List<Node> bucket = [];

        // Same-host devnet: N loopback nodes on distinct ports, plus M private 10/8 nodes, all beyond the table cap.
        for (int i = 0; i < 15; i++)
        {
            Node loopback = CreateNode(TestItem.PrivateKeys[i].PublicKey, "127.0.0.1", 30303 + i);
            Assert.That(limiter.TryAdmit(loopback, bucket.ToArray()), Is.True);
            Assert.That(limiter.WouldAdmit(loopback), Is.True);
            bucket.Add(loopback);
        }

        for (int i = 15; i < 25; i++)
        {
            Node lan = CreateNode(TestItem.PrivateKeys[i].PublicKey, $"10.0.{i - 15}.1");
            Assert.That(limiter.TryAdmit(lan, bucket.ToArray()), Is.True);
            Assert.That(limiter.WouldAdmit(lan), Is.True);
            bucket.Add(lan);
        }

        // LAN entries in the bucket consume no slot: two public nodes still fit alongside them.
        Node first = CreateNode(TestItem.PrivateKeys[30].PublicKey, "192.0.2.1");
        Node second = CreateNode(TestItem.PrivateKeys[31].PublicKey, "192.0.2.2");
        Assert.That(limiter.TryAdmit(first, bucket.ToArray()), Is.True);
        bucket.Add(first);
        Assert.That(limiter.TryAdmit(second, bucket.ToArray()), Is.True);
        bucket.Add(second);
        Assert.That(limiter.TryAdmit(CreateNode(TestItem.PrivateKeys[32].PublicKey, "192.0.2.3"), bucket.ToArray()), Is.False);

        // TEST-NET-1 is not LAN: the table cap still applies in a fresh bucket.
        AdmitNodes(limiter, 8, firstKeyIndex: 40);
        Assert.That(limiter.TryAdmit(CreateNode(TestItem.PrivateKeys[50].PublicKey, "192.0.2.200"), []), Is.False);
    }

    [Test]
    public void Disabled_filter_admits_everything_and_never_charges()
    {
        _networkConfig.FilterDiscoveryNodesByRecentIp = false;
        DiscoveryNodeLimiter limiter = CreateLimiter();
        Node[] bucket = [];

        for (int i = 0; i < 20; i++)
        {
            Node node = CreateNode(TestItem.PrivateKeys[i].PublicKey, $"192.0.2.{i + 1}");
            Assert.That(limiter.TryAdmit(node, bucket), Is.True);
        }

        Assert.That(limiter.WouldAdmit(CreateNode(TestItem.PrivateKeys[20].PublicKey, "192.0.2.201")), Is.True);

        // Nothing was charged while the filter was off, so enabling it starts from an empty table.
        _networkConfig.FilterDiscoveryNodesByRecentIp = true;
        Assert.That(limiter.WouldAdmit(CreateNode(TestItem.PrivateKeys[20].PublicKey, "192.0.2.201")), Is.True);
    }

    [Test]
    public void Refresh_of_a_held_identity_neither_rechecks_nor_recharges()
    {
        DiscoveryNodeLimiter limiter = CreateLimiter();
        Node node = CreateNode(TestItem.PrivateKeys[0].PublicKey, "192.0.2.1");
        Assert.That(limiter.TryAdmit(node, []), Is.True);

        Node[] saturatedBucket = [node, CreateNode(TestItem.PrivateKeys[1].PublicKey, "192.0.2.2")];
        Assert.That(limiter.TryAdmit(node, saturatedBucket), Is.True);

        // Still one charge only: nine more can join before the table limit is reached.
        AdmitNodes(limiter, 9, firstKeyIndex: 2);

        Assert.That(limiter.TryAdmit(CreateNode(TestItem.PrivateKeys[11].PublicKey, "192.0.2.200"), []), Is.False);
    }

    [Test]
    public void Address_change_into_a_saturated_subnet_is_refused_and_keeps_the_old_charge()
    {
        DiscoveryNodeLimiter limiter = CreateLimiter();
        Node moving = CreateNode(TestItem.PrivateKeys[0].PublicKey, "192.0.2.1");
        Assert.That(limiter.TryAdmit(moving, []), Is.True);

        AdmitNodes(limiter, subnet: "198.51.100", firstKeyIndex: 1);

        Node moved = CreateNode(moving.Id, "198.51.100.200");
        Assert.That(limiter.TryAdmit(moved, []), Is.False);

        // The old endpoint still holds its slot: the original subnet can take nine more before its cap.
        AdmitNodes(limiter, 9, firstKeyIndex: 30);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(limiter.TryAdmit(CreateNode(TestItem.PrivateKeys[20].PublicKey, "192.0.2.250"), []), Is.False);
            Assert.That(limiter.TryAdmit(CreateNode(TestItem.PrivateKeys[21].PublicKey, "198.51.100.250"), []), Is.False);
            // A refresh at the old endpoint is still accepted and does not double-charge.
            Assert.That(limiter.TryAdmit(moving, []), Is.True);
        }
    }

    [Test]
    public void Address_change_to_a_subnet_with_room_moves_the_charge()
    {
        DiscoveryNodeLimiter limiter = CreateLimiter();
        Node moving = CreateNode(TestItem.PrivateKeys[0].PublicKey, "192.0.2.1");
        Assert.That(limiter.TryAdmit(moving, []), Is.True);

        Assert.That(limiter.TryAdmit(CreateNode(moving.Id, "203.0.113.1"), []), Is.True);

        // The old subnet was released, so ten new nodes fit there again.
        AdmitNodes(limiter, firstKeyIndex: 1);
        Assert.That(limiter.TryAdmit(CreateNode(TestItem.PrivateKeys[20].PublicKey, "192.0.2.250"), []), Is.False);
    }

    [Test]
    public void Release_is_idempotent_and_frees_exactly_one_slot()
    {
        DiscoveryNodeLimiter limiter = CreateLimiter();
        Node[] bucket = [];
        Node first = CreateNode(TestItem.PrivateKeys[0].PublicKey, "192.0.2.1");
        Assert.That(limiter.TryAdmit(first, bucket), Is.True);
        AdmitNodes(limiter, DiscoveryNodeLimiter.TableLimit - 1, firstKeyIndex: 1, bucketNodes: bucket);

        limiter.Release(first);
        limiter.Release(first);

        Node newcomer = CreateNode(TestItem.PrivateKeys[10].PublicKey, "192.0.2.200");
        Assert.That(limiter.TryAdmit(newcomer, bucket), Is.True);
        Assert.That(limiter.TryAdmit(CreateNode(TestItem.PrivateKeys[11].PublicKey, "192.0.2.201"), bucket), Is.False);

        limiter.Release(CreateNode(TestItem.PrivateKeys[99].PublicKey, "203.0.113.1"));
        Assert.That(limiter.TryAdmit(CreateNode(TestItem.PrivateKeys[12].PublicKey, "192.0.2.202"), bucket), Is.False);
    }

    public enum Exemption
    {
        Self,
        Bootnode,
        StaticFlag,
        TrustedFlag,
        ManagedProvider
    }

    [TestCase(Exemption.Self)]
    [TestCase(Exemption.Bootnode)]
    [TestCase(Exemption.StaticFlag)]
    [TestCase(Exemption.TrustedFlag)]
    [TestCase(Exemption.ManagedProvider)]
    public void Exempt_nodes_are_admitted_and_never_consume_a_slot(Exemption exemption)
    {
        DiscoveryNodeLimiter limiter = CreateLimiter();
        AdmitNodes(limiter, DiscoveryNodeLimiter.TableLimit - 1);
        Node[] bucket = [];

        Node exempt = CreateExemptNode(exemption);
        Assert.That(limiter.TryAdmit(exempt, bucket), Is.True);
        Assert.That(limiter.WouldAdmit(exempt), Is.True);

        // Nine ordinary charges only, so one more ordinary node fits and the next is refused.
        Assert.That(limiter.TryAdmit(CreateNode(TestItem.PrivateKeys[20].PublicKey, "192.0.2.20"), bucket), Is.True);

        Assert.That(limiter.TryAdmit(CreateNode(TestItem.PrivateKeys[25].PublicKey, "192.0.2.100"), bucket), Is.False);
    }

    [Test]
    public void Revoked_exemption_recharges_on_next_refresh_when_there_is_room()
    {
        DiscoveryNodeLimiter limiter = CreateLimiter();
        Node node = CreateNode(TestItem.PrivateKeys[50].PublicKey, "192.0.2.50");
        Assert.That(limiter.TryAdmit(node, []), Is.True);

        // Gaining the exemption releases the charge: nine more ordinary nodes fit.
        _exemptions.IsExempt(node.Id).Returns(true);
        Assert.That(limiter.TryAdmit(node, []), Is.True);
        AdmitNodes(limiter, 9);

        // Losing it recharges on the next refresh; the table is full afterwards.
        _exemptions.IsExempt(node.Id).Returns(false);
        Assert.That(limiter.TryAdmit(node, []), Is.True);
        Assert.That(limiter.TryAdmit(CreateNode(TestItem.PrivateKeys[20].PublicKey, "192.0.2.200"), []), Is.False);
    }

    private static void AdmitNodes(
        DiscoveryNodeLimiter limiter,
        int count = DiscoveryNodeLimiter.TableLimit,
        string subnet = "192.0.2",
        int firstKeyIndex = 0,
        Node[]? bucketNodes = null)
    {
        Node[] bucket = bucketNodes ?? [];
        for (int i = 0; i < count; i++)
        {
            int keyIndex = firstKeyIndex + i;
            Assert.That(limiter.TryAdmit(CreateNode(TestItem.PrivateKeys[keyIndex].PublicKey, $"{subnet}.{keyIndex + 1}"), bucket), Is.True);
        }
    }

    private DiscoveryNodeLimiter CreateLimiter() => new(
        _currentNode,
        [_bootNode],
        _networkConfig,
        _exemptions);

    private Node CreateExemptNode(Exemption exemption)
    {
        PublicKey id = exemption switch
        {
            Exemption.Self => _currentNode.Id,
            Exemption.Bootnode => _bootNode.Id,
            _ => TestItem.PrivateKeys[50].PublicKey
        };

        Node node = CreateNode(id, "192.0.2.50");
        node.IsStatic = exemption == Exemption.StaticFlag;
        node.IsTrusted = exemption == Exemption.TrustedFlag;
        if (exemption == Exemption.ManagedProvider) _exemptions.IsExempt(id).Returns(true);

        return node;
    }

    private static Node CreateNode(PublicKey id, string host) => new(id, host, 30303);

    private static Node CreateNode(PublicKey id, string host, int port) => new(id, host, port);
}
