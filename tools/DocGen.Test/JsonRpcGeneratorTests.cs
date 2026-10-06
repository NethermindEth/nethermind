// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.DocGen.Test;

[TestFixture]
public class JsonRpcGeneratorTests
{
    private string _docsRoot = null!;

    private string RpcDirectory => Path.Combine(_docsRoot, "docs", "interacting", "json-rpc-ns");

    [OneTimeSetUp]
    public void GenerateDocs()
    {
        _docsRoot = Path.Combine(Path.GetTempPath(), $"nethermind-docgen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(RpcDirectory);
        File.WriteAllText(Path.Combine(RpcDirectory, "eth_subscribe.md"), string.Empty);
        File.WriteAllText(Path.Combine(RpcDirectory, "eth_unsubscribe.md"), string.Empty);

        JsonRpcGenerator.Generate(_docsRoot);
    }

    [OneTimeTearDown]
    public void TearDown() => Directory.Delete(_docsRoot, recursive: true);

    [TestCase("trace", "trace_get", "- `callType`:", "`includeInTrace`")]
    [TestCase("trace", "trace_filter", "- `creationMethod`:", "`isPrecompiled`")]
    [TestCase("trace", "trace_replayTransaction", "- `refundAddress`:", "`includeInTrace`")]
    [TestCase("trace", "trace_replayBlockTransactions", "- `creationMethod`:", "`includeInTrace`")]
    [TestCase("trace", "trace_call", "- `refundAddress`:", "`includeInTrace`")]
    [TestCase("trace", "trace_callMany", "- `callType`:", "`isPrecompiled`")]
    [TestCase("trace", "trace_rawTransaction", "- `creationMethod`:", "`isPrecompiled`")]
    [TestCase("trace", "trace_simulateV1", "- `traces`:", "- `calls`:")]
    [TestCase("debug", "debug_simulateV1", "- `traces`:", "- `calls`:")]
    [TestCase("eth", "eth_simulateV1", "- `calls`:", "- `traces`:")]
    public void Response_documents_wire_shape(string ns, string method, string documented, string notDocumented)
    {
        string response = ReadResponse(ns, method);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response, Does.Contain(documented));
            Assert.That(response, Does.Not.Contain(notDocumented));
        }
    }

    [TestCase("call", null)]
    [TestCase("create", null)]
    [TestCase("reward", null)]
    [TestCase("suicide", null)]
    [TestCase("call", "execution reverted")]
    public void Replay_response_documents_properties_written_by_each_converter_branch(string actionType, string? error)
    {
        ParityTxTraceFromReplay trace = new()
        {
            TransactionHash = Keccak.Zero,
            Action = new ParityTraceAction
            {
                Type = actionType,
                CallType = actionType,
                CreationMethod = actionType == "create" ? "create" : null,
                Error = error
            }
        };
        using JsonDocument wire = JsonDocument.Parse(new EthereumJsonSerializer().Serialize(trace));
        string response = ReadResponse("trace", "trace_replayTransaction");

        using (Assert.EnterMultipleScope())
        {
            AssertDocumentedProperties(wire.RootElement, response);
            AssertDocumentedProperties(wire.RootElement.GetProperty("trace")[0], response);
            AssertDocumentedProperties(wire.RootElement.GetProperty("trace")[0].GetProperty("action"), response);
        }
    }

    private static void AssertDocumentedProperties(JsonElement value, string response)
    {
        foreach (JsonProperty property in value.EnumerateObject())
            Assert.That(response, Does.Contain($"- `{property.Name}`:"), $"serialized property {property.Name} is missing from the response docs");
    }

    private string ReadResponse(string ns, string method)
    {
        string doc = File.ReadAllText(Path.Combine(RpcDirectory, $"{ns}.md"));
        int start = doc.IndexOf($"### {method}\n", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), $"{method} is not documented");

        int end = doc.IndexOf("\n### ", start + 1, StringComparison.Ordinal);
        string methodDoc = end < 0 ? doc[start..] : doc[start..end];
        int response = methodDoc.IndexOf("<TabItem value=\"response\"", StringComparison.Ordinal);
        Assert.That(response, Is.GreaterThanOrEqualTo(0), $"{method} has no response");

        return methodDoc[response..];
    }
}
