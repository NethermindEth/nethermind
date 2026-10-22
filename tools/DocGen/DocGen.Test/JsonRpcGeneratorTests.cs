// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Serialization.Json;
using Nethermind.State.Proofs;
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
    [TestCase("trace", "trace_simulateV1", "- `traces`:", "- `calls`:")]
    [TestCase("debug", "debug_simulateV1", "- `traces`:", "- `calls`:")]
    [TestCase("eth", "eth_simulateV1", "- `calls`:", "- `traces`:")]
    [TestCase("eth", "eth_createAccessList", "- `accessList`: array of _object_", "- `accessList`: _object_")]
    [TestCase("eth", "eth_getProof", "- `storageHash`:", "- `storageRoot`:")]
    [TestCase("eth", "eth_syncing", "`result`: `false` when not syncing, else _object_", "- `isSyncing`:")]
    [TestCase("debug", "debug_getRawBlockAccessList", "`result`: _string_ (hex data)", "- `memory`:")]
    [TestCase("debug", "debug_traceTransaction", "- `structLogs`:", "- `customTracerResult`:")]
    [TestCase("debug", "debug_simulateV1", "- `traces`: array of _object_ (the tracer's own result instead when `tracer` is set)", "- `customTracerResult`:")]
    public void Response_documents_wire_shape(string ns, string method, string documented, string notDocumented)
    {
        string response = ReadResponse(ns, method);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response, Does.Contain(documented));
            Assert.That(response, Does.Not.Contain(notDocumented));
        }
    }

    [TestCase("eth", "eth_getProof", "3. `blockParameter`: _string_ (block number or hash or either of `earliest`, `finalized`, `latest`, `pending`, or `safe`), _object_ (EIP-1898 `{blockHash, requireCanonical}` or `{blockNumber}`), or _integer_ (block number, if `JsonRpc.StrictHexFormat` is `false`)", "`safe`)\n")]
    [TestCase("eth", "eth_getLogs", "- `topics`: array of (_string_ (hash), array of _string_ (hash), or `null`)", "EIP-1898")]
    [TestCase("eth", "eth_newFilter", "- `address`: _string_ (address), array of _string_ (address), or `null`", "EIP-1898")]
    [TestCase("eth", "eth_newFilter", "- `fromBlock`: _string_ (block number or hash or either of `earliest`, `finalized`, `latest`, `pending`, or `safe`) or _integer_ (block number)", "- `topics`: array of array")]
    [TestCase("eth", "eth_newFilter", "- `blockHash`: _string_ (hash, not allowed with `fromBlock` or `toBlock`)", "- `topics`: array of array")]
    [TestCase("eth", "eth_getStorageValues", "1. `requests`: map of address to array of _string_ (hex storage slot), 1 to 1024 slots in total", "- `entries`:")]
    [TestCase("trace", "trace_filter", "- `fromBlock`: _string_ (block number or either of `earliest`, `finalized`, `latest`, `pending`, or `safe`), _object_ (EIP-1898 `{blockNumber}`)", "requireCanonical")]
    public void Parameters_document_accepted_input(string ns, string method, string documented, string notDocumented)
    {
        string parameters = ReadParameters(ns, method);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parameters, Does.Contain(documented));
            Assert.That(parameters, Does.Not.Contain(notDocumented));
        }
    }

    [Test]
    public void Trace_callMany_documents_calls_as_wire_array_of_pairs()
    {
        string doc = File.ReadAllText(Path.Combine(RpcDirectory, "trace.md"));
        int start = doc.IndexOf("### trace_callMany\n", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0));
        int end = doc.IndexOf("\n### ", start + 1, StringComparison.Ordinal);
        string methodDoc = end < 0 ? doc[start..] : doc[start..end];
        int parametersStart = methodDoc.IndexOf("<TabItem value=\"params\"", StringComparison.Ordinal);
        Assert.That(parametersStart, Is.GreaterThanOrEqualTo(0));
        int parametersEnd = methodDoc.IndexOf("</TabItem>", parametersStart, StringComparison.Ordinal);
        Assert.That(parametersEnd, Is.GreaterThan(parametersStart));
        string parameters = methodDoc[parametersStart..parametersEnd];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parameters, Does.Contain("1. `calls`: array of [_object_ (transaction, same as `call` in `trace_call`), array of _string_ (trace type)] pairs"));
            Assert.That(parameters, Does.Not.Contain("- `calls`:"));
            Assert.That(parameters, Does.Contain("2. `blockParameter`: _string_"));
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
            AssertDocumentedProperties(wire.RootElement, response, 2);
            string traceResponse = ReadPropertyBlock(response, "trace", 2);
            AssertDocumentedProperties(wire.RootElement.GetProperty("trace")[0], traceResponse, 4);
            string actionResponse = ReadPropertyBlock(traceResponse, "action", 4);
            AssertDocumentedProperties(wire.RootElement.GetProperty("trace")[0].GetProperty("action"), actionResponse, 6);
        }
    }

    private static IEnumerable<TestCaseData> ConverterWrittenResults()
    {
        yield return new TestCaseData("eth", "eth_getProof", new AccountProof());
        yield return new TestCaseData("eth", "eth_syncing", new SyncingResult { IsSyncing = true });
        yield return new TestCaseData("debug", "debug_traceTransaction", new GethLikeTxTrace { TxHash = Keccak.Zero });
    }

    [TestCaseSource(nameof(ConverterWrittenResults))]
    public void Response_documents_properties_written_by_class_converter(string ns, string method, object result)
    {
        using JsonDocument wire = JsonDocument.Parse(new EthereumJsonSerializer().Serialize(result));

        AssertDocumentedProperties(wire.RootElement, ReadResponse(ns, method), 2);
    }

    [Test]
    public void Create_access_list_response_documents_access_list_items()
    {
        AccessList accessList = new AccessList.Builder().AddAddress(Address.Zero).AddStorage(UInt256.One).Build();
        AccessListResultForRpc result = new(AccessListForRpc.FromAccessList(accessList), UInt256.One, null);
        using JsonDocument wire = JsonDocument.Parse(new EthereumJsonSerializer().Serialize(result));
        string accessListResponse = ReadPropertyBlock(ReadResponse("eth", "eth_createAccessList"), "accessList", 2);

        using (Assert.EnterMultipleScope())
        {
            AssertDocumentedProperties(wire.RootElement.GetProperty("accessList")[0], accessListResponse, 4);
            Assert.That(accessListResponse, Does.Contain("- `storageKeys`: array of _string_ (32-byte hex data)"));
        }
    }

    private static void AssertDocumentedProperties(JsonElement value, string response, int indentation)
    {
        foreach (JsonProperty property in value.EnumerateObject())
            Assert.That(response.Split('\n'), Has.Some.StartsWith($"{new string(' ', indentation)}- `{property.Name}`:"),
                $"serialized property {property.Name} is missing from its response object");
    }

    private static string ReadPropertyBlock(string response, string property, int indentation)
    {
        string[] lines = response.Split('\n');
        string prefix = $"{new string(' ', indentation)}- `{property}`:";
        int start = Array.FindIndex(lines, line => line.StartsWith(prefix, StringComparison.Ordinal));
        Assert.That(start, Is.GreaterThanOrEqualTo(0), $"{property} is missing from its response object");
        int end = start + 1;
        string childIndentation = new(' ', indentation + 2);
        while (end < lines.Length && (string.IsNullOrWhiteSpace(lines[end]) || lines[end].StartsWith(childIndentation, StringComparison.Ordinal)))
            end++;
        return string.Join('\n', lines[(start + 1)..end]);
    }

    private string ReadParameters(string ns, string method)
    {
        string doc = File.ReadAllText(Path.Combine(RpcDirectory, $"{ns}.md"));
        int start = doc.IndexOf($"### {method}\n", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), $"{method} is not documented");

        int end = doc.IndexOf("\n### ", start + 1, StringComparison.Ordinal);
        string methodDoc = end < 0 ? doc[start..] : doc[start..end];
        int parameters = methodDoc.IndexOf("<TabItem value=\"params\"", StringComparison.Ordinal);
        Assert.That(parameters, Is.GreaterThanOrEqualTo(0), $"{method} has no parameters");

        return methodDoc[parameters..methodDoc.IndexOf("</TabItem>", parameters, StringComparison.Ordinal)];
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
