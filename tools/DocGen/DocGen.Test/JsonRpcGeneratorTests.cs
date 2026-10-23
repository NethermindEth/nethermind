// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Parity;
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
    [TestCase("eth", "eth_getBlockByHash", "- `transactions`: array of _string_ (hash), or array of _object_ (transaction, same as `result` in `eth_getTransactionByHash`) when full transactions are requested", "- `hashes`:")]
    [TestCase("debug", "debug_getBadBlocks", "Each `block` carries full transaction objects.", "- `hashes`:")]
    [TestCase("eth", "eth_createAccessList", "- `accessList`: array of _object_", "- `accessList`: _object_")]
    [TestCase("eth", "eth_getProof", "- `storageHash`:", "- `storageRoot`:")]
    [TestCase("eth", "eth_syncing", "`result`: `false` when not syncing, else _object_", "- `isSyncing`:")]
    [TestCase("debug", "debug_getRawBlockAccessList", "`result`: _string_ (hex data)", "- `memory`:")]
    [TestCase("debug", "debug_traceTransaction", "- `structLogs`:", "- `customTracerResult`:")]
    [TestCase("eth", "eth_getHeaderByNumber", "- `hash`: _string_ (hash) or `null`", "- `baseFeePerGas`: _string_ (hex integer) or `null`")]
    [TestCase("debug", "debug_simulateV1", "- `traces`: array of _object_ (the tracer's own result instead when `tracer` is set)", "- `customTracerResult`:")]
    [TestCase("eth", "eth_getTransactionByHash", "- `gas`: _string_ (hex integer)\n", "(transaction type) or `null`")]
    [TestCase("eth", "eth_getStorageValues", "`result`: map of address to array of _string_ (32-byte hex data)", "- `slots`:")]
    [TestCase("debug", "debug_getRawReceipts", "`result`: array of _string_ (hex data)", "- `receipts`:")]
    [TestCase("txpool", "txpool_content", "each a map of sender address to a map of nonce (decimal, or hash for an EIP-8250 keyed transaction) to _object_ (transaction)", "- `info`:")]
    [TestCase("txpool", "txpool_inspect", "to _string_ (`sender: value wei + gas × gasPrice gas`)", "- `info`:")]
    [TestCase("trace", "trace_replayTransaction", "- `balance`: `\"=\"` (unchanged), or _object_ `{\"+\": value}` (added)", "- `after`:")]
    [TestCase("trace", "trace_replayTransaction", "- `ops`: array of _object_", "- `operations`:")]
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
    [TestCase("eth", "eth_getProof", "2. `storageKeys`: array of _string_ (hex storage slot), or a _string_ holding that array as JSON", "- `count`:")]
    [TestCase("eth", "eth_getStorageAt", "2. `positionIndex`: _string_ (hex storage slot)", "- `value`:")]
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
    }

    [TestCaseSource(nameof(ConverterWrittenResults))]
    public void Response_documents_properties_written_by_class_converter(string ns, string method, object result)
    {
        using JsonDocument wire = JsonDocument.Parse(new EthereumJsonSerializer().Serialize(result));

        AssertDocumentedPropertiesMatch(wire.RootElement, ReadResponse(ns, method), 2);
    }

    [TestCase("debug_traceTransaction", false)]
    [TestCase("debug_traceBlockByNumber", true)]
    public void Trace_response_documents_struct_log_entries(string method, bool isBlockTrace)
    {
        GethLikeTxTrace trace = CreateTraceWithStructLog();
        using JsonDocument wire = JsonDocument.Parse(new EthereumJsonSerializer().Serialize(
            isBlockTrace ? new GethLikeTxTraceStreamingResult([trace]) : (object)trace));
        string response = ReadResponse("debug", method);
        JsonElement traceElement = isBlockTrace ? wire.RootElement[0].GetProperty("result") : wire.RootElement;
        int indentation = isBlockTrace ? 4 : 2;
        string traceResponse = isBlockTrace ? ReadPropertyBlock(response, "result", 2) : response;

        using (Assert.EnterMultipleScope())
        {
            if (isBlockTrace)
                AssertDocumentedPropertiesMatch(wire.RootElement[0], response, 2);

            AssertDocumentedPropertiesMatch(traceElement, traceResponse, indentation);
            AssertDocumentedPropertiesMatch(traceElement.GetProperty("structLogs")[0],
                ReadPropertyBlock(traceResponse, "structLogs", indentation), indentation + 2);
            Assert.That(traceResponse.Split('\n'), Has.Some.EqualTo($"{new string(' ', indentation)}- `gas`: _integer_"));
        }
    }

    private static GethLikeTxTrace CreateTraceWithStructLog() => new()
    {
        TxHash = Keccak.Zero,
        Entries =
        [
            new GethTxTraceEntry
            {
                Opcode = "SSTORE",
                Error = "out of gas",
                Refund = 1,
                Stack = new ReadOnlyMemory<byte>(new byte[32]),
                Memory = new ReadOnlyMemory<byte>(new byte[32]),
                Storage = new Dictionary<UInt256, UInt256> { [UInt256.One] = UInt256.One },
                ReturnData = new ReadOnlyMemory<byte>([1])
            }
        ]
    };

    [Test]
    public void Create_access_list_response_documents_access_list_items()
    {
        AccessList accessList = new AccessList.Builder().AddAddress(Address.Zero).AddStorage(UInt256.One).Build();
        AccessListResultForRpc result = new(AccessListForRpc.FromAccessList(accessList), UInt256.One, null);
        using JsonDocument wire = JsonDocument.Parse(new EthereumJsonSerializer().Serialize(result));
        string accessListResponse = ReadPropertyBlock(ReadResponse("eth", "eth_createAccessList"), "accessList", 2);

        using (Assert.EnterMultipleScope())
        {
            AssertDocumentedPropertiesMatch(wire.RootElement.GetProperty("accessList")[0], accessListResponse, 4);
            Assert.That(accessListResponse, Does.Contain("- `storageKeys`: array of _string_ (32-byte hex data)"));
        }
    }

    [Test]
    public void Generated_writer_is_recognized_by_interface_name(
        [Values(typeof(BlockForRpc), typeof(ReceiptForRpc), typeof(LogEntryForRpc))] Type type) =>
        Assert.That(EthereumJsonSerializer.JsonOptions.GetTypeInfo(type).Converter.GetType()
            .GetInterface(JsonRpcGenerator.GeneratedJsonWriterInterface), Is.Not.Null);

    private static IEnumerable<TestCaseData> ResultsWithNullMembers()
    {
        yield return new TestCaseData("eth", "eth_getHeaderByNumber",
            new BlockHeaderForRpc(new BlockHeader(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, null!, 0, 0, 0, 0, [])))
            .SetArgDisplayNames("pending header");
        yield return new TestCaseData("parity", "parity_pendingTransactions",
            new ParityTransaction(new Transaction { SenderAddress = Address.Zero, Signature = new Signature(1, 1, 27) }, [], null!))
            .SetArgDisplayNames("pending contract creation");
        yield return new TestCaseData("parity", "parity_pendingTransactions",
            new ParityTransaction(new Transaction { SenderAddress = Address.Zero }, [], null!))
            .SetArgDisplayNames("unsigned frame transaction");
        yield return new TestCaseData("eth", "eth_getBlockByNumber", new BlockForRpc())
            .SetArgDisplayNames("pending block");
        yield return new TestCaseData("eth", "eth_getTransactionReceipt", new ReceiptForRpc())
            .SetArgDisplayNames("receipt without recipient");
        yield return new TestCaseData("trace", "trace_get", ParityTxTraceFromStore.FromTxTrace(new ParityLikeTxTrace
        {
            BlockHash = Keccak.Zero,
            Action = new ParityTraceAction { Type = "reward", RewardType = "block", Author = Address.Zero, Result = null, TraceAddress = CappedArray<int>.Empty }
        }).Single()).SetArgDisplayNames("reward trace");
        yield return new TestCaseData("trace", "trace_get", ParityTxTraceFromStore.FromTxTrace(new ParityLikeTxTrace
        {
            BlockHash = Keccak.Zero,
            Action = new ParityTraceAction { Type = "reward", RewardType = "block", Author = Address.Zero, Result = null }
        }).Single()).SetArgDisplayNames("reward trace without trace address");
        yield return new TestCaseData("trace", "trace_replayTransaction", new ParityTxTraceFromReplay
        {
            Action = new ParityTraceAction
            {
                Type = "call",
                CallType = "call",
                From = Address.Zero,
                To = Address.Zero,
                Input = CappedArray<byte>.Empty,
                Error = "Reverted",
                Result = null,
                TraceAddress = CappedArray<int>.Empty
            }
        }).SetArgDisplayNames("failed call without state diff or vm trace");
        yield return new TestCaseData("trace", "trace_replayTransaction", new ParityTxTraceFromReplay
        {
            Output = [],
            StateChanges = [],
            Action = new ParityTraceAction
            {
                Type = "call",
                CallType = "call",
                From = Address.Zero,
                To = Address.Zero,
                Input = CappedArray<byte>.Empty,
                Result = new ParityTraceResult { Output = [] },
                TraceAddress = CappedArray<int>.Empty
            },
            VmTrace = new ParityVmTrace { Code = [], Operations = [new ParityVmOperationTrace { Halted = true }, new ParityVmOperationTrace()] }
        }).SetArgDisplayNames("vm trace with halted and empty operations");
    }

    [TestCaseSource(nameof(ResultsWithNullMembers))]
    public void Response_documents_members_written_as_null(string ns, string method, object result)
    {
        using JsonDocument wire = JsonDocument.Parse(new EthereumJsonSerializer().Serialize(result));
        int nullProperties;

        using (Assert.EnterMultipleScope())
            nullProperties = AssertNullPropertiesDocumented(wire.RootElement, ReadResponse(ns, method), 2);

        Assert.That(nullProperties, Is.Not.Zero);
    }

    /// <summary>Asserts each null property of <paramref name="value"/> and of its nested objects is documented as nullable.</summary>
    /// <returns>The number of null properties found.</returns>
    private static int AssertNullPropertiesDocumented(JsonElement value, string response, int indentation)
    {
        string[] lines = response.Split('\n');
        int nullProperties = 0;

        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Null)
            {
                nullProperties++;
                Assert.That(lines.Any(line =>
                        line.StartsWith($"{new string(' ', indentation)}- `{property.Name}`:", StringComparison.Ordinal) &&
                        line.EndsWith(" or `null`", StringComparison.Ordinal)), Is.True,
                    $"{property.Name} is written as null but not documented as nullable");
                continue;
            }

            JsonElement[] children = property.Value.ValueKind switch
            {
                JsonValueKind.Object => [property.Value],
                JsonValueKind.Array => [.. property.Value.EnumerateArray().Where(item => item.ValueKind is JsonValueKind.Object)],
                _ => []
            };

            if (children.Length == 0)
                continue;

            string childResponse = ReadPropertyBlock(response, property.Name, indentation);
            foreach (JsonElement child in children)
                nullProperties += AssertNullPropertiesDocumented(child, childResponse, indentation + 2);
        }

        return nullProperties;
    }

    private static void AssertDocumentedProperties(JsonElement value, string response, int indentation)
    {
        foreach (JsonProperty property in value.EnumerateObject())
            Assert.That(response.Split('\n'), Has.Some.StartsWith($"{new string(' ', indentation)}- `{property.Name}`:"),
                $"serialized property {property.Name} is missing from its response object");
    }

    // Holds only for converters that write every member on every call
    private static void AssertDocumentedPropertiesMatch(JsonElement value, string response, int indentation)
    {
        AssertDocumentedProperties(value, response, indentation);

        string prefix = $"{new string(' ', indentation)}- `";

        foreach (string line in response.Split('\n').Where(l => l.StartsWith(prefix, StringComparison.Ordinal)))
        {
            string name = line[prefix.Length..line.IndexOf('`', prefix.Length)];
            Assert.That(value.TryGetProperty(name, out _), Is.True, $"documented property {name} is not serialized");
        }
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
