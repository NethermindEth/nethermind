// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class Hash256ArrayConverterTests
{
    private static readonly JsonSerializerOptions s_options = new() { Converters = { new Hash256ArrayConverter() } };

    [Test]
    public void Roundtrip_EmptyArray()
    {
        const string json = "[]";
        Hash256?[]? hashes = JsonSerializer.Deserialize<Hash256?[]>(json, s_options);
        hashes.Should().NotBeNull().And.BeEmpty();
        JsonSerializer.Serialize(hashes, s_options).Should().Be(json);
    }

    [Test]
    public void Roundtrip_Null()
    {
        const string json = "null";
        Hash256?[]? hashes = JsonSerializer.Deserialize<Hash256?[]>(json, s_options);
        hashes.Should().BeNull();
        JsonSerializer.Serialize(hashes, s_options).Should().Be(json);
    }

    [Test]
    public void Roundtrip_SingleHash()
    {
        const string json = "[\"0x0123456789abcdeffedcba98765432100123456789abcdeffedcba9876543210\"]";
        Hash256?[]? hashes = JsonSerializer.Deserialize<Hash256?[]>(json, s_options);

        hashes.Should().NotBeNull();
        hashes!.Length.Should().Be(1);
        hashes[0]!.ToString().Should().Be("0x0123456789abcdeffedcba98765432100123456789abcdeffedcba9876543210");

        JsonSerializer.Serialize(hashes, s_options).Should().Be(json);
    }

    [Test]
    public void Roundtrip_MultipleHashes()
    {
        const string json = "[\"0x" + "00" + "0123456789abcdeffedcba9876543210" + "0123456789abcdeffedcba98765432" + "\"," +
                            "\"0x" + "ff" + "0123456789abcdeffedcba9876543210" + "0123456789abcdeffedcba98765432" + "\"]";
        Hash256?[]? hashes = JsonSerializer.Deserialize<Hash256?[]>(json, s_options);

        hashes.Should().NotBeNull();
        hashes!.Length.Should().Be(2);
        hashes[0]!.Bytes[0].Should().Be(0x00);
        hashes[1]!.Bytes[0].Should().Be(0xff);

        JsonSerializer.Serialize(hashes, s_options).Should().Be(json);
    }

    [Test]
    public void Read_NullElement_PreservedAsNull()
    {
        const string json = "[null,\"0x0123456789abcdeffedcba98765432100123456789abcdeffedcba9876543210\"]";
        Hash256?[]? hashes = JsonSerializer.Deserialize<Hash256?[]>(json, s_options);

        hashes.Should().NotBeNull();
        hashes!.Length.Should().Be(2);
        hashes[0].Should().BeNull();
        hashes[1].Should().NotBeNull();
    }

    [Test]
    public void Read_WrongLengthElement_Throws()
    {
        const string json = "[\"0xabcd\"]";
        FluentActions.Invoking(() => JsonSerializer.Deserialize<Hash256?[]>(json, s_options))
            .Should().Throw<JsonException>();
    }

    [Test]
    public void Write_NullElement_EmitsJsonNull()
    {
        Hash256?[] hashes = [null, new Hash256("0x0123456789abcdeffedcba98765432100123456789abcdeffedcba9876543210")];
        string json = JsonSerializer.Serialize(hashes, s_options);
        json.Should().Be("[null,\"0x0123456789abcdeffedcba98765432100123456789abcdeffedcba9876543210\"]");
    }
}
