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
        Assert.That(nodeRecord.GetValue<int>(EnrContentKey.Udp), Is.EqualTo(12345));
        Assert.That(nodeRecord.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1), Is.EqualTo(new CompressedPublicKey(new byte[33])));
    }

    [Test]
    public void Get_value_or_obj_can_handle_missing_values()
    {
        NodeRecord nodeRecord = new();
        Assert.That(nodeRecord.GetValue<int>(EnrContentKey.Udp), Is.Null);
        Assert.That(nodeRecord.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1), Is.Null);
    }

    [Test]
    public void Cannot_get_enr_string_when_signature_missing()
    {
        NodeRecord nodeRecord = new();
        Assert.Throws<Exception>(() => _ = nodeRecord.EnrString);
    }

    [Test]
    public void Discovery_endpoint_rejects_ipv4_with_udp6_only()
    {
        IPAddress ip = IPAddress.Parse("192.0.2.1");
        NodeRecord nodeRecord = new();
        nodeRecord.SetEntry(new IpEntry(ip));
        nodeRecord.SetEntry(new Udp6Entry(30304));

        Assert.That(nodeRecord.DiscoveryIp, Is.Null);
        Assert.That(nodeRecord.DiscoveryPort, Is.Null);
    }

    [Test]
    public void Discovery_endpoint_uses_udp_as_ipv6_fallback()
    {
        IPAddress ip = IPAddress.Parse("2001:db8::1");
        NodeRecord nodeRecord = new();
        nodeRecord.SetEntry(new Ip6Entry(ip));
        nodeRecord.SetEntry(new UdpEntry(30303));

        Assert.That(nodeRecord.DiscoveryIp, Is.EqualTo(ip));
        Assert.That(nodeRecord.DiscoveryPort, Is.EqualTo(30303));
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
