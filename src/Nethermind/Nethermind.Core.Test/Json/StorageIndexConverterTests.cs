// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text.Json;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class StorageIndexConverterTests
{
    [TestCase("\"0x0100000000000000000000000000000000000000000000000000000000000000\"", "0100000000000000000000000000000000000000000000000000000000000000")]
    [TestCase("\"0x0000000000000000000000000000000000000000000000000000000000000001\"", "1")]
    [TestCase("\"0x2\"", "2")]
    [TestCase("\"0x0\"", "0")]
    [TestCase("\"0xff\"", "ff")]
    [TestCase("\"0xabc\"", "abc")]
    [TestCase("\"0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff\"", "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    public void Accepts_data_key(string json, string expectedHex)
    {
        StorageIndex result = JsonSerializer.Deserialize<StorageIndex>(json);
        Assert.That(result.Value, Is.EqualTo(UInt256.Parse(expectedHex, NumberStyles.HexNumber)));
    }

    [Test]
    public void Rejects_key_longer_than_32_bytes() =>
        Assert.That(
            () => JsonSerializer.Deserialize<StorageIndex>("\"0x" + new string('f', 65) + "\""),
            Throws.InstanceOf<JsonException>());

    [Test]
    public void Rejects_non_string() =>
        Assert.That(() => JsonSerializer.Deserialize<StorageIndex>("2"), Throws.InstanceOf<JsonException>());

    [Test]
    public void Converts_to_underlying_uint256()
    {
        StorageIndex index = new(new UInt256(42));
        UInt256 value = index;
        Assert.That(value, Is.EqualTo(new UInt256(42)));
    }
}
