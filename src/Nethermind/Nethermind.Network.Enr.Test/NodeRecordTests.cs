// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
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
        nodeRecord.SetEntry(new Secp256K1Entry(
            new CompressedPublicKey(new byte[33])));
        nodeRecord.GetValue<int>(EnrContentKey.Udp).Should().Be(12345);
        nodeRecord.GetObj<CompressedPublicKey>(EnrContentKey.Secp256K1).Should().Be(
            new CompressedPublicKey(new byte[33]));
    }

    [Test]
    public void Get_value_or_obj_can_handle_missing_values()
    {
        NodeRecord nodeRecord = new();
        nodeRecord.GetValue<int>(EnrContentKey.Udp).Should().BeNull();
        nodeRecord.GetObj<CompressedPublicKey>(EnrContentKey.Secp256K1).Should().BeNull();
    }

    [Test]
    public void Cannot_get_enr_string_when_signature_missing()
    {
        NodeRecord nodeRecord = new();
        Assert.Throws<Exception>(() => _ = nodeRecord.EnrString);
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
