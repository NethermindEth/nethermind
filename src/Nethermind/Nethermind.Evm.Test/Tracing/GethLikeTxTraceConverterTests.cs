// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

public class GethLikeTxTraceConverterTests
{
    private readonly EthereumJsonSerializer _serializer = new();

    [Test]
    public void Write_null()
    {
        string result = _serializer.Serialize((GethLikeTxTrace?)null);

        Assert.That(result, Is.EqualTo("null"));
    }

    [TestCaseSource(nameof(TraceAndJsonSource))]
    public void Write_traces(GethLikeTxTrace trace, string json)
    {
        string result = _serializer.Serialize(trace);

        Assert.That(JsonElement.DeepEquals(
            JsonDocument.Parse(result).RootElement,
            JsonDocument.Parse(json).RootElement),
            result);
    }

    [TestCaseSource(nameof(CustomValueTracerResults))]
    public void Write_custom_tracer_result(object value, string expected)
    {
        GethLikeTxTrace trace = new()
        {
            CustomTracerResult = new GethLikeCustomTrace { Value = value }
        };

        string result = _serializer.Serialize(trace);

        Assert.That(JsonElement.DeepEquals(
            JsonDocument.Parse(result).RootElement,
            JsonDocument.Parse(expected).RootElement),
            result);
    }

    [Test]
    public void Read_null()
    {
        GethLikeTxTrace result = _serializer.Deserialize<GethLikeTxTrace>("null");

        Assert.That(result, Is.Null);
    }

    [TestCaseSource(nameof(TraceAndJsonSource))]
    public void Read_traces(GethLikeTxTrace expectedTrace, string json)
    {
        GethLikeTxTrace result = _serializer.Deserialize<GethLikeTxTrace>(json);

        result.Should().BeEquivalentTo(expectedTrace);
    }


    [TestCaseSource(nameof(CustomValueTracerResults))]
    public void Read_custom_tracer_result_throws(object expectedValue, string json)
    {
        Assert.Throws<JsonException>(() => _serializer.Deserialize<GethLikeTxTrace>(json));
    }

    private static IEnumerable<TestCaseData> TraceAndJsonSource()
    {
        yield return new TestCaseData(
            new GethLikeTxTrace { Gas = 1, ReturnValue = [0x01] },
            """{ "gas": 1, "failed": false, "returnValue": "0x01", "structLogs": [] }""")
            .SetName("Gas1_NoEntries");
        yield return new TestCaseData(
            new GethLikeTxTrace
            {
                Gas = 100,
                Failed = false,
                ReturnValue = [0x01, 0x02, 0x03],
                Entries =
                [
                    new()
                    {
                        Storage = new()
                        {
                            { "1".PadLeft(64, '0'), "2".PadLeft(64, '0') },
                            { "3".PadLeft(64, '0'), "4".PadLeft(64, '0') },
                        },
                        Memory =
                        [
                            "5".PadLeft(64, '0'),
                            "6".PadLeft(64, '0')
                        ],
                        Stack =
                        [
                            "7".PadLeft(64, '0'),
                            "8".PadLeft(64, '0')
                        ],
                        Opcode = "STOP",
                        Gas = 22000,
                        GasCost = 1,
                        Depth = 1
                    }
                ]
            },
            """
            {
              "gas" : 100,
              "failed" : false,
              "returnValue" : "0x010203",
              "structLogs" : [ {
                "pc" : 0,
                "op" : "STOP",
                "gas" : 22000,
                "gasCost" : 1,
                "depth" : 1,
                "error" : null,
                "stack" : [ "0000000000000000000000000000000000000000000000000000000000000007", "0000000000000000000000000000000000000000000000000000000000000008" ],
                "memory" : [ "0000000000000000000000000000000000000000000000000000000000000005", "0000000000000000000000000000000000000000000000000000000000000006" ],
                "storage" : {
                  "0000000000000000000000000000000000000000000000000000000000000001" : "0000000000000000000000000000000000000000000000000000000000000002",
                  "0000000000000000000000000000000000000000000000000000000000000003" : "0000000000000000000000000000000000000000000000000000000000000004"
                }
              } ]
            }
            """)
            .SetName("Gas100_1Entry");
    }

    private static IEnumerable<TestCaseData> CustomValueTracerResults()
    {
        yield return new TestCaseData(1, "1").SetName("Custom_Int");
        yield return new TestCaseData("1", "\"1\"").SetName("Custom_String");
        yield return new TestCaseData(new[] { 1, 2 }, "[1, 2]").SetName("Custom_Array");
        yield return new TestCaseData(new { a = 1, b = 2 }, "{ \"a\": 1, \"b\": 2 }").SetName("Custom_Object");
    }
}
