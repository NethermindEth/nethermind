// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class UInt256DictionaryKeyConverterTests
{
    private static readonly JsonSerializerOptions Options = EthereumJsonSerializer.CreateOptions(true);

    [Test]
    public void ReadJson_NestedValidJson_ReturnsCorrectDictionary()
    {
        string json = @"{
            ""blockStateCalls"": [
                {
                    ""stateOverrides"": {
                        ""0xc100000000000000000000000000000000000000"": {
                            ""state"": {
                                ""0x0000000000000000000000000000000000000000000000000000000000000000"": ""0x1200000000000000000000000000000000000000000000000000000000000000""
                            }
                        }
                    }
                }
            ]
        }";

        var result = JsonSerializer.Deserialize<
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

        Assert.That(result, Is.Not.Null);

        // Check for correct top-level key
        Assert.IsTrue(result!.ContainsKey("blockStateCalls"));
        // Additional assertions to check the structure and data of the nested dictionaries
        var blockStateCalls = result["blockStateCalls"];
        Assert.IsNotNull(blockStateCalls);
        Assert.IsTrue(1 == blockStateCalls.Count);

        var stateOverrides = blockStateCalls[0];
        Assert.IsNotNull(stateOverrides);
        Assert.IsTrue(stateOverrides.ContainsKey("stateOverrides"));

        var stateOverridesContents = stateOverrides["stateOverrides"];
        var soKey = Bytes.FromHexString("0xc100000000000000000000000000000000000000").ToUInt256(); ;
        var state = stateOverridesContents[soKey];
        Assert.IsNotNull(state);
        Assert.IsTrue(state.ContainsKey("state"));

        var stateInner = state["state"];
        var key = Bytes.FromHexString("0x0000000000000000000000000000000000000000").ToUInt256();
        var hashValue = stateInner[key];
        Assert.IsTrue("0x1200000000000000000000000000000000000000000000000000000000000000" == hashValue.ToString());
    }

    [Test]
    public void ReadJson_NullJson_ReturnsNull()
    {
        Dictionary<UInt256, Hash256>? result = JsonSerializer.Deserialize<Dictionary<UInt256, Hash256>>("null", Options);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void ReadJson_EmptyObject_ReturnsEmptyDictionary()
    {
        Dictionary<UInt256, Hash256>? result = JsonSerializer.Deserialize<Dictionary<UInt256, Hash256>>("{}", Options);
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void WriteJson_Dictionary_ThrowsNotSupportedException()
    {
        var dictionary = new Dictionary<UInt256, Hash256>
        {
            { new UInt256(1), new Hash256("0x0000000000000000000000000000000000000000000000000000000000000002") }
        };
        var serialised = JsonSerializer.Serialize(dictionary, Options);
        var deserialised = JsonSerializer.Deserialize<Dictionary<UInt256, Hash256>>(serialised, Options);

        Assert.IsTrue(deserialised!.ContainsKey(new UInt256(1)));
        Assert.IsTrue(deserialised!.ContainsValue(new Hash256("0x0000000000000000000000000000000000000000000000000000000000000002")));
    }
}
