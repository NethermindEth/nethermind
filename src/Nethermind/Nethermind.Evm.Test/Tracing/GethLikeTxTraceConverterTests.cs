// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.GethStyle.Custom;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

public class GethLikeTxTraceConverterTests
{
    private readonly EthereumJsonSerializer _serializer = new();

    [Test]
    public void Write_null()
    {
        var result = _serializer.Serialize((GethLikeTxTrace?)null);

        Assert.That(result, Is.EqualTo("null"));
    }

    [TestCaseSource(nameof(TraceAndJsonSource))]
    public void Write_traces(GethLikeTxTrace trace, string json)
    {
        var result = _serializer.Serialize(trace);

        Assert.That(JsonElement.DeepEquals(
            JsonDocument.Parse(result).RootElement,
            JsonDocument.Parse(json).RootElement),
            result);
    }

    [TestCaseSource(nameof(CustomValueTracerResults))]
    public void Write_custom_tracer_result(object value, string expected)
    {
        var trace = new GethLikeTxTrace
        {
            CustomTracerResult = new GethLikeCustomTrace { Value = value }
        };

        var result = _serializer.Serialize(trace);

        Assert.That(JsonElement.DeepEquals(
            JsonDocument.Parse(result).RootElement,
            JsonDocument.Parse(expected).RootElement),
            result);
    }

    [Test]
    public void Read_null()
    {
        var result = _serializer.Deserialize<GethLikeTxTrace>("null");

        Assert.That(result, Is.Null);
    }

    [TestCaseSource(nameof(TraceAndJsonSource))]
    public void Read_traces(GethLikeTxTrace expectedTrace, string json)
    {
        var result = _serializer.Deserialize<GethLikeTxTrace>(json);

        result.Should().BeEquivalentTo(expectedTrace);
    }


    [TestCaseSource(nameof(CustomValueTracerResults))]
    public void Read_custom_tracer_result_throws(object expectedValue, string json)
    {
        Assert.Throws<JsonException>(() => _serializer.Deserialize<GethLikeTxTrace>(json));
    }

    private static IEnumerable<object[]> TraceAndJsonSource()
    {
        yield return [
            new GethLikeTxTrace { Gas = 1, ReturnValue = [0x01] },
            """{ "gas": 1, "failed": false, "returnValue": "0x01", "structLogs": [] }"""];
        yield return [
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
            """
        ];
    }

    private static IEnumerable<object[]> CustomValueTracerResults()
    {
        yield return [1, "1"];
        yield return ["1", "\"1\""];
        yield return [new[] { 1, 2 }, "[1, 2]"];
        yield return [new { a = 1, b = 2 }, "{ \"a\": 1, \"b\": 2 }"];
    }
}
