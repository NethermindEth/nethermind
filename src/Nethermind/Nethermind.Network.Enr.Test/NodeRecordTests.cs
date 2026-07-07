// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
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
