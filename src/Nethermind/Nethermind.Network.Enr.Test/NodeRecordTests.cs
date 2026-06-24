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
    public void Cannot_get_enr_string_when_signature_missing()
    {
        NodeRecord nodeRecord = new();
        Assert.Throws<Exception>(() => _ = nodeRecord.EnrString);
    }

    [Test]
    public void Enr_request_sequence_tracks_single_active_request()
    {
        NodeRecord nodeRecord = new();

        Assert.That(nodeRecord.TryRequestEnrSequence(5), Is.True);
        Assert.That(nodeRecord.TryRequestEnrSequence(4), Is.False);
        Assert.That(nodeRecord.TryRequestEnrSequence(7), Is.False);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodeRecord.RequestingEnrSequence, Is.EqualTo(7));
            Assert.That(nodeRecord.TryClearEnrRequest(5), Is.False);
            Assert.That(nodeRecord.RequestingEnrSequence, Is.EqualTo(7));
            Assert.That(nodeRecord.TryClearEnrRequest(7), Is.True);
            Assert.That(nodeRecord.RequestingEnrSequence, Is.Zero);
        }
    }

    [TestCase("192.0.2.1", "", -1, 30304, "", -1)]
    [TestCase("", "2001:db8::1", 30303, -1, "2001:db8::1", 30303)]
    public void Discovery_endpoint_uses_expected_ip_udp_fallback(string ip, string ip6, int udp, int udp6, string expectedIp, int expectedPort)
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
            Assert.That(nodeRecord.DiscoveryIp, Is.Null);
            Assert.That(nodeRecord.DiscoveryPort, Is.Null);
        }
        else
        {
            Assert.That(nodeRecord.DiscoveryIp, Is.EqualTo(IPAddress.Parse(expectedIp)));
            Assert.That(nodeRecord.DiscoveryPort, Is.EqualTo(expectedPort));
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
