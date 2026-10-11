// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Text.Json;

using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public class FilterTests
{
    public static IEnumerable JsonTests
    {
        get
        {
            yield return new TestCaseData("{}",
                new Filter
                {
                    FromBlock = BlockParameter.Earliest,
                    ToBlock = BlockParameter.Latest,
                });

            yield return new TestCaseData(
                JsonSerializer.Serialize(
                    new
                    {
                        topics = new object?[]
                        {
                            null,
                            "0xe194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7",
                            new[]
                            {
                                "0x000500002bd87daa34d8ff0daf3465c96044d8f6667614850000000000000001",
                                "0xe194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7"
                            }
                        }
                    }),
                new Filter
                {
                    FromBlock = BlockParameter.Earliest,
                    ToBlock = BlockParameter.Latest,
                    Topics =
                    [
                        null,
                        [new("0xe194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7")],
                        [
                            new Hash256("0x000500002bd87daa34d8ff0daf3465c96044d8f6667614850000000000000001"),
                            new Hash256("0xe194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7")
                        ]
                    ]
                });

            yield return new TestCaseData(
                JsonSerializer.Serialize(
                    new
                    {
                        address = "0xc2d77d118326c33bbe36ebeabf4f7ed6bc2dda5c",
                        fromBlock = "0x1143ade",
                        topics = new object?[]
                        {
                            "0xe194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7",
                            null,
                            null,
                            "0x000500002bd87daa34d8ff0daf3465c96044d8f6667614850000000000000001"
                        }
                    }),
                new Filter
                {
                    Address = [new Address("0xc2d77d118326c33bbe36ebeabf4f7ed6bc2dda5c")],
                    FromBlock = new BlockParameter(0x1143ade),
                    ToBlock = BlockParameter.Latest,
                    Topics =
                    [
                        [new("0xe194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7")],
                        null,
                        null,
                        [new("0x000500002bd87daa34d8ff0daf3465c96044d8f6667614850000000000000001")]
                    ]
                });

            // Non-canonical spellings that must keep binding: uppercase, no prefix, JSON escapes, odd length.
            yield return new TestCaseData(
                """
                {
                    "address": [
                        "0xC2D77D118326C33BBE36EBEABF4F7ED6BC2DDA5C",
                        "b7705ae4c6f81b66cdb323c65f4e8133690fc099",
                        "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
                        "0x2d77d118326c33bbe36ebeabf4f7ed6bc2dda5c"
                    ],
                    "topics": [
                        "e194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7",
                        ["0x000500002bd87daa34d8ff0daf3465c96044d8f6667614850000000000000001", "0x194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7"]
                    ]
                }
                """,
                new Filter
                {
                    Address =
                    [
                        new Address("0xc2d77d118326c33bbe36ebeabf4f7ed6bc2dda5c"),
                        new Address("0xb7705ae4c6f81b66cdb323c65f4e8133690fc099"),
                        new Address("0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358"),
                        new Address("0x02d77d118326c33bbe36ebeabf4f7ed6bc2dda5c")
                    ],
                    FromBlock = BlockParameter.Earliest,
                    ToBlock = BlockParameter.Latest,
                    Topics =
                    [
                        [new("0xe194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7")],
                        [
                            new Hash256("0x000500002bd87daa34d8ff0daf3465c96044d8f6667614850000000000000001"),
                            new Hash256("0x0194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7")
                        ]
                    ]
                });

            string blockHash = "0x892a8b3ccc78359e059e67ec44c83bfed496721d48c2d1dd929d6e4cd6559d35";
            BlockParameter blockParam = BlockParameterConverter.GetBlockParameter(blockHash);

            yield return new TestCaseData(
                JsonSerializer.Serialize(new { blockHash }),
                new Filter
                {
                    FromBlock = blockParam,
                    ToBlock = blockParam,
                });
        }
    }

    [TestCaseSource(nameof(JsonTests))]
    public void FromJson_parses_correctly(string json, Filter expectation)
    {
        Filter filter = new();
        using JsonDocument doc = JsonDocument.Parse(json);
        filter.ReadJson(doc.RootElement, EthereumJsonSerializer.JsonOptions);
        Assert.That(filter.Address, Is.EqualTo(expectation.Address));
        Assert.That(filter.FromBlock, Is.EqualTo(expectation.FromBlock));
        Assert.That(filter.ToBlock, Is.EqualTo(expectation.ToBlock));
        Assert.That(filter.Topics, Is.EqualTo(expectation.Topics));
    }

    [TestCase("""{"address":"0xc2d77d118326c33bbe36ebeabf4f7ed6bc2dda5g"}""", typeof(FormatException))]
    [TestCase("""{"address":["0xc2d77d118326c33bbe36ebeabf4f7ed6bc2dda"]}""", typeof(ArgumentException))]
    [TestCase("""{"address":["0XC2D77D118326C33BBE36EBEABF4F7ED6BC2DDA5C"]}""", typeof(FormatException))]
    [TestCase("""{"address":[123]}""", typeof(ArgumentException))]
    [TestCase("""{"address":[null]}""", typeof(ArgumentException))]
    [TestCase("""{"address":"0x"}""", typeof(ArgumentException))]
    [TestCase("""{"topics":["0xe194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fz7"]}""", typeof(FormatException))]
    [TestCase("""{"topics":[["0xe194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7aa"]]}""", typeof(ArgumentException))]
    [TestCase("""{"topics":[[true]]}""", typeof(FormatException))]
    public void ReadJson_rejects_invalid_address_or_topic(string json, Type expectedException)
    {
        Filter filter = new();
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.That(() => filter.ReadJson(doc.RootElement, EthereumJsonSerializer.JsonOptions), Throws.TypeOf(expectedException));
    }

    [Test]
    public void ReadJson_materializes_topics_before_json_document_is_disposed([Values] bool filterAsString)
    {
        string filterJson = JsonSerializer.Serialize(
            new
            {
                topics = new object?[]
                {
                    "0xe194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7",
                    null,
                    new[]
                    {
                        "0x000500002bd87daa34d8ff0daf3465c96044d8f6667614850000000000000001",
                        "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef"
                    }
                }
            });
        string json = filterAsString ? JsonSerializer.Serialize(filterJson) : filterJson;
        Filter filter = new();

        using (JsonDocument doc = JsonDocument.Parse(json))
        {
            filter.ReadJson(doc.RootElement, EthereumJsonSerializer.JsonOptions);
        }

        Assert.That(filter.Topics, Is.EqualTo(new Hash256[]?[]
        {
            [new("0xe194ef610f9150a2db4110b3db5116fd623175dca3528d7ae7046a1042f84fe7")],
            null,
            [
                new("0x000500002bd87daa34d8ff0daf3465c96044d8f6667614850000000000000001"),
                new("0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef")
            ]
        }));
    }

    [Test]
    public void ReadJson_throws_when_filter_string_exceeds_limit()
    {
        Filter filter = new();
        string oversized = $"\"{new string('a', 1_000_001)}\"";
        using JsonDocument doc = JsonDocument.Parse(oversized);
        Action act = () => filter.ReadJson(doc.RootElement, EthereumJsonSerializer.JsonOptions);
        Assert.That(act, Throws.TypeOf<ArgumentException>().With.Message.Contains(@"exceeds maximum"));
    }
}
