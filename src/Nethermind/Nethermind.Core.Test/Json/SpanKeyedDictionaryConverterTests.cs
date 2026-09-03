// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class SpanKeyedDictionaryConverterTests
{
    private readonly EthereumJsonSerializer _serializer = new();

    [Test]
    public void UInt256_keyed_dictionary_round_trips_with_the_same_json_shape()
    {
        Dictionary<UInt256, Hash256> storage = new()
        {
            [UInt256.Zero] = TestItem.KeccakA,
            [new UInt256(0x1234)] = TestItem.KeccakB,
            [UInt256.MaxValue] = TestItem.KeccakC,
        };

        string json = _serializer.Serialize(storage);

        Assert.That(json, Is.EqualTo(
            $"{{\"0x0\":\"{TestItem.KeccakA}\",\"0x1234\":\"{TestItem.KeccakB}\",\"0x{new string('f', 64)}\":\"{TestItem.KeccakC}\"}}"));
        Assert.That(_serializer.Deserialize<Dictionary<UInt256, Hash256>>(json), Is.EquivalentTo(storage));
    }

    [Test]
    public void Address_keyed_dictionary_round_trips_with_nested_values()
    {
        Dictionary<Address, Dictionary<UInt256, Hash256>> overrides = new()
        {
            [TestItem.AddressA] = new Dictionary<UInt256, Hash256> { [UInt256.One] = TestItem.KeccakA },
            [TestItem.AddressB] = [],
        };

        string json = _serializer.Serialize(overrides);
        Dictionary<Address, Dictionary<UInt256, Hash256>> deserialized = _serializer.Deserialize<Dictionary<Address, Dictionary<UInt256, Hash256>>>(json)!;

        Assert.That(json, Is.EqualTo(
            $"{{\"{TestItem.AddressA.ToString().ToLowerInvariant()}\":{{\"0x1\":\"{TestItem.KeccakA}\"}},\"{TestItem.AddressB.ToString().ToLowerInvariant()}\":{{}}}}"));
        Assert.That(deserialized.Keys, Is.EquivalentTo(overrides.Keys));
        Assert.That(deserialized[TestItem.AddressA], Is.EquivalentTo(overrides[TestItem.AddressA]));
        Assert.That(deserialized[TestItem.AddressB], Is.Empty);
    }

    [Test]
    public void Reads_keys_in_any_accepted_hex_form_and_keeps_the_last_duplicate()
    {
        string json = $"{{\"0x01\":\"{TestItem.KeccakA}\",\"0x1\":\"{TestItem.KeccakB}\",\"0x000000000000000000000000000000000000000000000000000000000000000a\":null}}";

        Dictionary<UInt256, Hash256?> storage = _serializer.Deserialize<Dictionary<UInt256, Hash256?>>(json)!;

        Assert.That(storage, Has.Count.EqualTo(2));
        Assert.That(storage[UInt256.One], Is.EqualTo(TestItem.KeccakB));
        Assert.That(storage[new UInt256(10)], Is.Null);
    }

    [Test]
    public void Null_and_empty_dictionaries_behave_like_the_default_converter()
    {
        Assert.That(_serializer.Deserialize<Dictionary<Address, Hash256>>("null"), Is.Null);
        Assert.That(_serializer.Deserialize<Dictionary<Address, Hash256>>("{}"), Is.Empty);
        Assert.That(_serializer.Serialize(new Dictionary<UInt256, Hash256>()), Is.EqualTo("{}"));
    }

    [TestCase("[]")]
    [TestCase("{\"0x1\":\"0x1111111111111111111111111111111111111111111111111111111111111111\"")]
    [TestCase("{\"0x1\"}")]
    public void Rejects_malformed_objects(string json) =>
        Assert.Throws<JsonException>(() => _serializer.Deserialize<Dictionary<UInt256, Hash256>>(json));

    // Key and value converters keep reporting their own errors, as they did through the built-in dictionary converter.
    [TestCase("{\"not-a-key\":\"0x00\"}", typeof(FormatException))]
    [TestCase("{\"0x1\":\"0x00\"}", typeof(ArgumentException))]
    public void Rejects_malformed_keys_and_values(string json, Type exception) =>
        Assert.Throws(exception, () => _serializer.Deserialize<Dictionary<UInt256, Hash256>>(json));
}
