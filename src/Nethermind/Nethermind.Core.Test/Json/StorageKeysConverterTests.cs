// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Text.Json;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class StorageKeysConverterTests
{
    [Test]
    public void Keeps_request_order_and_duplicates()
    {
        StorageKeys keys = JsonSerializer.Deserialize<StorageKeys>("[\"0x2\",\"0x1\",\"0x1\",\"0x0000000000000000000000000000000000000000000000000000000000000001\"]")!;
        Assert.That(keys.ToArray(), Is.EqualTo(new UInt256[] { 2, 1, 1, 1 }));
    }

    [Test]
    public void Reads_empty_hex_as_zero() =>
        Assert.That(JsonSerializer.Deserialize<StorageKeys>("[\"0x\"]")!.ToArray(), Is.EqualTo(new[] { UInt256.Zero }));

    [TestCase("[\"0xzz\"]")]
    [TestCase("[\"0x10000000000000000000000000000000000000000000000000000000000000000\"]")]
    [TestCase("[\"1\"]")]
    [TestCase("[1]")]
    public void Rejects_invalid_key(string json) =>
        Assert.That(() => JsonSerializer.Deserialize<StorageKeys>(json), Throws.Exception);
}
