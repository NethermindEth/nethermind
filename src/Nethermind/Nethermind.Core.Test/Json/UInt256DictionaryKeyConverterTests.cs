// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class UInt256DictionaryKeyConverterTests
{
    private static readonly JsonSerializerOptions Options = EthereumJsonSerializer.JsonOptionsIndented;

    [Test]
    public void ReadJson_NestedValidJson_ReturnsCorrectDictionary()
    {
        string json = """
        {
            "blockStateCalls": [
                {
                    "stateOverrides": {
                        "0xc100000000000000000000000000000000000000": {
                            "state": {
                                "0x0000000000000000000000000000000000000000000000000000000000000000": "0x1200000000000000000000000000000000000000000000000000000000000000"
                            }
                        }
                    }
                }
            ]
        }
        """;

        Dictionary<string, List<Dictionary<string, Dictionary<UInt256, Dictionary<string, Dictionary<UInt256, Hash256>>>>>>? result = JsonSerializer.Deserialize<
            Dictionary<string, List<
                Dictionary<string,
                    Dictionary<UInt256,
                        Dictionary<string,
                            Dictionary<UInt256, Hash256>
                        >
                    >
                >
            >>
        >(json, Options);

        Dictionary<UInt256, Hash256> expectedState = new()
        {
            { new UInt256(0), new Hash256("0x1200000000000000000000000000000000000000000000000000000000000000") }
        };
        Dictionary<UInt256, Dictionary<string, Dictionary<UInt256, Hash256>>> expectedStateOverrides = new()
        {
            {
                new UInt256(Bytes.FromHexString("0xc100000000000000000000000000000000000000")),
                new Dictionary<string, Dictionary<UInt256, Hash256>>
                {
                    {"state", expectedState}
                }
            }
        };
        List<Dictionary<string, Dictionary<UInt256, Dictionary<string, Dictionary<UInt256, Hash256>>>>> expectedBlockStateCalls = new()
        {
            new()
            {
                {"stateOverrides", expectedStateOverrides}
            }
        };
        Dictionary<string, List<Dictionary<string, Dictionary<UInt256, Dictionary<string, Dictionary<UInt256, Hash256>>>>>> expected = new()
        {
            {"blockStateCalls", expectedBlockStateCalls}
        };

        result.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void ReadJson_NullJson_ReturnsNull()
    {
        Dictionary<UInt256, Hash256>? result = JsonSerializer.Deserialize<Dictionary<UInt256, Hash256>>("null", Options);

        result.Should().BeNull();
    }

    [Test]
    public void ReadJson_EmptyObject_ReturnsEmptyDictionary()
    {
        Dictionary<UInt256, Hash256>? result = JsonSerializer.Deserialize<Dictionary<UInt256, Hash256>>("{}", Options);
        Dictionary<UInt256, Hash256> empty = new();

        result.Should().BeEquivalentTo(empty);
    }

    [Test]
    public void WriteJson_Dictionary_SerializeAndDeserialize()
    {
        Dictionary<UInt256, Hash256> dictionary = new()
        {
            { new UInt256(1), new Hash256("0x0000000000000000000000000000000000000000000000000000000000000002") }
        };
        string serialised = JsonSerializer.Serialize(dictionary, Options);
        Dictionary<UInt256, Hash256>? deserialised = JsonSerializer.Deserialize<Dictionary<UInt256, Hash256>>(serialised, Options);

        deserialised.Should().BeEquivalentTo(dictionary);
    }

    [Test]
    public void WriteJson_Dictionary_SerializedCorrectly()
    {
        Dictionary<UInt256, UInt256> dictionary = new()
        {
            { new UInt256(1), new UInt256(12345) }
        };
        string serialised = JsonSerializer.Serialize(dictionary, Options);
        serialised = serialised.Replace("\r\n", "\n");
        string expected = """
        {
          "0x1": "0x3039"
        }
        """;

        serialised.Should().BeEquivalentTo(expected);
    }
}
