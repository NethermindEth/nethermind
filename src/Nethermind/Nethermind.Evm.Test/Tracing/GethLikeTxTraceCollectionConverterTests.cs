// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

public class GethLikeTxTraceCollectionConverterTests
{
    private readonly EthereumJsonSerializer _serializer = new();

    [Test]
    public void Write_null()
    {
        var result = _serializer.Serialize((GethLikeTxTraceCollection?)null);

        Assert.That(result, Is.EqualTo("null"));
    }

    [Test]
    public void Write_empty()
    {
        var collection = new GethLikeTxTraceCollection([]);
        var result = _serializer.Serialize(collection);

        Assert.That(result, Is.EqualTo("[]"));
    }

    [TestCaseSource(nameof(TracesAndJsonsSource))]
    public void Write_with_traces_with_tx_hash(GethLikeTxTrace trace, string json)
    {
        var expected = $"""[{json}]""";

        var collection = new GethLikeTxTraceCollection([trace]);
        var result = _serializer.Serialize(collection);

        Assert.That(JsonElement.DeepEquals(
            JsonDocument.Parse(result).RootElement,
            JsonDocument.Parse(expected).RootElement),
            result);
    }

    [Test]
    public void Read_null()
    {
        var json = "null";
        var result = _serializer.Deserialize<GethLikeTxTraceCollection>(json);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void Read_empty()
    {
        var json = "[]";
        var result = _serializer.Deserialize<GethLikeTxTraceCollection>(json);

        Assert.That(result.Count, Is.EqualTo(0));
    }


    [TestCaseSource(nameof(TracesAndJsonsSource))]
    public void Read_with_traces(GethLikeTxTrace expectedTrace, string json)
    {
        var result = _serializer.Deserialize<GethLikeTxTraceCollection>($"""[{json}]""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Count, Is.EqualTo(1));
            Assert.That(result.First().Gas, Is.EqualTo(expectedTrace.Gas));
            Assert.That(result.First().ReturnValue, Is.EqualTo(expectedTrace.ReturnValue));
            Assert.That(result.First().TxHash, Is.EqualTo(expectedTrace.TxHash));
        });
    }

    private static IEnumerable<object[]> TracesAndJsonsSource()
    {
        yield return [
            new GethLikeTxTrace { Gas = 1, ReturnValue = [0x01], TxHash = null },
            """
            {
                "result": { "gas": 1, "failed": false, "returnValue": "0x01", "structLogs": [] },
                "txHash": null
            }
            """
        ];
        yield return [
            new GethLikeTxTrace { Gas = 2, ReturnValue = [0x02], TxHash = Hash256.Zero },
            """
            {
                "result": { "gas": 2, "failed": false, "returnValue": "0x02", "structLogs": [] },
                "txHash": "0x0000000000000000000000000000000000000000000000000000000000000000"
            }
            """
        ];
        yield return [
            new GethLikeTxTrace { Gas = 3, ReturnValue = [0x03], TxHash = Keccak.Compute("A") },
            """
            {
                "result": { "gas": 3, "failed": false, "returnValue": "0x03", "structLogs": [] },
                "txHash": "0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760"
            }
            """
        ];
    }
}
