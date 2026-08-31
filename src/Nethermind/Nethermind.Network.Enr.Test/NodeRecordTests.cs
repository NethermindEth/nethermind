// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Sockets;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Network.Enr.Test;

[TestFixture]
public class NodeRecordTests
{
    [Test]
    public void Get_value_or_obj_can_return_when_not_null()
    {
        NodeRecord nodeRecord = new();
        nodeRecord.SetEntry(new UdpEntry(12345));
        nodeRecord.SetEntry(new SecP256k1Entry(
            new CompressedPublicKey(new byte[33])));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodeRecord.GetValue<int>(EnrContentKey.Udp), Is.EqualTo(12345));
            Assert.That(nodeRecord.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1), Is.EqualTo(new CompressedPublicKey(new byte[33])));
        }
    }

    [Test]
    public void Get_value_or_obj_can_handle_missing_values()
    {
        NodeRecord nodeRecord = new();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodeRecord.GetValue<int>(EnrContentKey.Udp), Is.Null);
            Assert.That(nodeRecord.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1), Is.Null);
        }
    }

    [Test]
    public void Cannot_encode_to_string_when_signature_missing()
    {
        NodeRecord nodeRecord = new();
        Assert.Throws<Exception>(() => _ = nodeRecord.ToString());
    }

    [TestCase("192.0.2.1", "", -1, 30304, "192.0.2.1", -1)]
    [TestCase("192.0.2.1", "2001:db8::1", -1, 30304, "2001:db8::1", 30304)]
    [TestCase("", "2001:db8::1", 30303, -1, "2001:db8::1", 30303)]
    public void Ip_is_common_and_discovery_port_uses_matching_family(string ip, string ip6, int udp, int udp6, string expectedIp, int expectedPort)
    {
        NodeRecord nodeRecord = new();

        if (!string.IsNullOrEmpty(ip))
        {
            nodeRecord.SetEntry(new IpEntry(IPAddress.Parse(ip)));
        }

        if (!string.IsNullOrEmpty(ip6))
        {
            nodeRecord.SetEntry(new Ip6Entry(IPAddress.Parse(ip6)));
        }

        if (udp >= 0)
        {
            nodeRecord.SetEntry(new UdpEntry(udp));
        }

        if (udp6 >= 0)
        {
            nodeRecord.SetEntry(new Udp6Entry(udp6));
        }

        if (expectedPort < 0)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(nodeRecord.Ip, Is.EqualTo(IPAddress.Parse(expectedIp)));
                Assert.That(nodeRecord.DiscoveryPort, Is.Null);
            }
        }
        else
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(nodeRecord.Ip, Is.EqualTo(IPAddress.Parse(expectedIp)));
                Assert.That(nodeRecord.DiscoveryPort, Is.EqualTo(expectedPort));
            }
        }
    }

    [Test]
    public void Can_select_endpoints_by_address_family()
    {
        NodeRecord nodeRecord = new();
        nodeRecord.SetEntry(new IpEntry(IPAddress.Parse("192.0.2.1")));
        nodeRecord.SetEntry(new Ip6Entry(IPAddress.Parse("2001:db8::1")));
        nodeRecord.SetEntry(new TcpEntry(30303));
        nodeRecord.SetEntry(new UdpEntry(30304));
        nodeRecord.SetEntry(new Tcp6Entry(30305));
        nodeRecord.SetEntry(new Udp6Entry(30306));

        bool hasIpV4Udp = nodeRecord.TryGetDiscoveryEndpoint(AddressFamily.InterNetwork, out IPEndPoint? ipV4Udp);
        bool hasIpV4Tcp = nodeRecord.TryGetTcpEndpoint(AddressFamily.InterNetwork, out IPEndPoint? ipV4Tcp);
        bool hasIpV6Udp = nodeRecord.TryGetDiscoveryEndpoint(AddressFamily.InterNetworkV6, out IPEndPoint? ipV6Udp);
        bool hasIpV6Tcp = nodeRecord.TryGetTcpEndpoint(AddressFamily.InterNetworkV6, out IPEndPoint? ipV6Tcp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hasIpV4Udp, Is.True);
            Assert.That(ipV4Udp, Is.EqualTo(IPEndPoint.Parse("192.0.2.1:30304")));
            Assert.That(hasIpV4Tcp, Is.True);
            Assert.That(ipV4Tcp, Is.EqualTo(IPEndPoint.Parse("192.0.2.1:30303")));
            Assert.That(hasIpV6Udp, Is.True);
            Assert.That(ipV6Udp, Is.EqualTo(IPEndPoint.Parse("[2001:db8::1]:30306")));
            Assert.That(hasIpV6Tcp, Is.True);
            Assert.That(ipV6Tcp, Is.EqualTo(IPEndPoint.Parse("[2001:db8::1]:30305")));
            Assert.That(nodeRecord.TryGetDiscoveryEndpoint(AddressFamily.Unspecified, out _), Is.False);
        }
    }

    [Test]
    public void Tcp_endpoints_are_extracted_per_family_from_dual_stack_record()
    {
        NodeRecord nodeRecord = CreateEndpointRecord(
            ip: IPAddress.Parse("192.0.2.1"),
            tcp: 30303,
            ip6: IPAddress.Parse("2001:db8::1"),
            tcp6: 30305);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodeRecord.TryGetTcpEndpoint(out IPEndPoint? ipv4), Is.True);
            Assert.That(ipv4, Is.EqualTo(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 30303)));
            Assert.That(nodeRecord.TryGetTcp6Endpoint(out IPEndPoint? ipv6), Is.True);
            Assert.That(ipv6, Is.EqualTo(new IPEndPoint(IPAddress.Parse("2001:db8::1"), 30305)));
        }
    }

    [Test]
    public void Tcp6_endpoint_falls_back_to_the_shared_tcp_port()
    {
        NodeRecord nodeRecord = CreateEndpointRecord(
            ip: IPAddress.Parse("192.0.2.1"),
            tcp: 30303,
            ip6: IPAddress.Parse("2001:db8::1"),
            tcp6: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodeRecord.TryGetTcpEndpoint(out IPEndPoint? ipv4), Is.True);
            Assert.That(ipv4, Is.EqualTo(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 30303)));
            Assert.That(nodeRecord.TryGetTcp6Endpoint(out IPEndPoint? ipv6), Is.True);
            Assert.That(ipv6, Is.EqualTo(new IPEndPoint(IPAddress.Parse("2001:db8::1"), 30303)));
        }
    }

    [TestCase(true, 30304)]
    [TestCase(false, 30304)]
    public void Tcp_endpoint_falls_back_to_ipv6_entries_when_ipv4_tcp_is_missing(bool withTcp6, int expectedPort)
    {
        NodeRecord nodeRecord = new();
        nodeRecord.SetEntry(new Ip6Entry(IPAddress.Parse("2001:db8::1")));
        if (withTcp6)
        {
            nodeRecord.SetEntry(new Tcp6Entry(expectedPort));
        }
        else
        {
            nodeRecord.SetEntry(new TcpEntry(expectedPort));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodeRecord.TryGetTcpEndpoint(out IPEndPoint? endpoint), Is.True);
            Assert.That(endpoint, Is.EqualTo(new IPEndPoint(IPAddress.Parse("2001:db8::1"), expectedPort)));
            Assert.That(nodeRecord.TryGetTcp6Endpoint(out IPEndPoint? ipv6), Is.True);
            Assert.That(ipv6, Is.EqualTo(new IPEndPoint(IPAddress.Parse("2001:db8::1"), expectedPort)));
        }
    }

    [Test]
    public void Cannot_select_ipv4_mapped_address_from_ip6_entry()
    {
        NodeRecord nodeRecord = new();
        nodeRecord.SetEntry(new Ip6Entry(IPAddress.Parse("::ffff:192.0.2.1")));
        nodeRecord.SetEntry(new Tcp6Entry(30303));
        nodeRecord.SetEntry(new Udp6Entry(30304));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodeRecord.TryGetDiscoveryEndpoint(AddressFamily.InterNetworkV6, out _), Is.False);
            Assert.That(nodeRecord.TryGetTcpEndpoint(AddressFamily.InterNetworkV6, out _), Is.False);
            Assert.That(nodeRecord.TryGetDiscoveryEndpoint(out _), Is.False);
            Assert.That(nodeRecord.TryGetTcpEndpoint(out _), Is.False);
        }
    }

    [Test]
    public void Tcp_endpoints_are_missing_without_usable_port_entries()
    {
        NodeRecord nodeRecord = CreateEndpointRecord(
            ip: IPAddress.Parse("192.0.2.1"),
            tcp: null,
            ip6: IPAddress.Parse("2001:db8::1"),
            tcp6: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodeRecord.TryGetTcpEndpoint(out _), Is.False);
            Assert.That(nodeRecord.TryGetTcp6Endpoint(out _), Is.False);
        }
    }

    [Test]
    public void Udp6_endpoint_prefers_udp6_over_the_shared_udp_port()
    {
        NodeRecord nodeRecord = CreateEndpointRecord(
            ip: null,
            tcp: null,
            ip6: IPAddress.Parse("2001:db8::1"),
            tcp6: null);
        nodeRecord.SetEntry(new UdpEntry(30306));
        nodeRecord.SetEntry(new Udp6Entry(30307));

        Assert.That(nodeRecord.TryGetUdp6Endpoint(out IPEndPoint? endpoint), Is.True);
        Assert.That(endpoint, Is.EqualTo(new IPEndPoint(IPAddress.Parse("2001:db8::1"), 30307)));
    }

    [Test]
    public void Udp6_endpoint_falls_back_to_the_shared_udp_port()
    {
        NodeRecord nodeRecord = CreateEndpointRecord(
            ip: null,
            tcp: null,
            ip6: IPAddress.Parse("2001:db8::1"),
            tcp6: null);
        nodeRecord.SetEntry(new UdpEntry(30306));

        Assert.That(nodeRecord.TryGetUdp6Endpoint(out IPEndPoint? endpoint), Is.True);
        Assert.That(endpoint, Is.EqualTo(new IPEndPoint(IPAddress.Parse("2001:db8::1"), 30306)));
    }

    private static NodeRecord CreateEndpointRecord(IPAddress? ip, int? tcp, IPAddress? ip6, int? tcp6)
    {
        NodeRecord nodeRecord = new();
        if (ip is not null)
        {
            nodeRecord.SetEntry(new IpEntry(ip));
        }

        if (ip6 is not null)
        {
            nodeRecord.SetEntry(new Ip6Entry(ip6));
        }

        if (tcp is not null)
        {
            nodeRecord.SetEntry(new TcpEntry(tcp.Value));
        }

        if (tcp6 is not null)
        {
            nodeRecord.SetEntry(new Tcp6Entry(tcp6.Value));
        }

        return nodeRecord;
    }

    [Test]
    public void Enr_content_entry_has_hash_code()
    {
        EnrContentEntry a = IdEntry.Instance;
        _ = a.GetHashCode();
    }

    [Test]
    public void Enr_content_entry_has_to_string()
    {
        EnrContentEntry a = IdEntry.Instance;
        _ = a.ToString();
    }
}
