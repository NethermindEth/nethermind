// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.FourByte;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Noop;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Prestate;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using Nethermind.Core.Crypto;
using Newtonsoft.Json.Linq;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    [Test]
    public async Task Debug_traceTransaction_validates_timeout_after_prefix_before_stream_commit(
        [Values("valid", "bad", "prefix-error")] string scenario)
    {
        bool invalidTimeout = scenario != "valid";
        ExecutionCount counter = new();
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(builder => builder.AddDecorator<Nethermind.Evm.TransactionProcessing.ITransactionProcessorAdapter>(
                (_, inner) => new BudgetCountingAdapter(inner, counter)));
        ulong nonce = chain.ReadOnlyState.GetNonce(TestItem.AddressB);
        Transaction[] transactions = new Transaction[3];
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = Build.A.Transaction.WithTo(TestItem.AddressC).WithNonce(nonce + (ulong)i).WithValue(1)
                .WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        }
        Block block = await chain.AddBlock(transactions);
        Assert.That(block.Transactions, Has.Length.EqualTo(3));
        System.IO.Pipelines.Pipe pipe = new(new System.IO.Pipelines.PipeOptions(pauseWriterThreshold: 0));
        counter.Enabled = true;
        List<Hash256> executed = [];
        counter.OnExecute = transaction =>
        {
            executed.Add(transaction.Hash!);
            Assert.That(pipe.Writer.UnflushedBytes, Is.Zero, "no transport bytes during prefix/target Execute");
            if (scenario == "prefix-error") throw new InvalidOperationException("prefix failure");
        };
        try
        {
            using JsonRpcResponse response = await RpcTest.TestRequest(chain.DebugRpcModule, "debug_traceTransaction",
                transactions[1].Hash!, new { timeout = invalidTimeout ? "bad" : "5s", streamMode = true });
            await JsonRpcResponseWriter.WriteAsync(pipe.Writer, response, Nethermind.Serialization.Json.EthereumJsonSerializer.JsonOptions,
                System.Threading.CancellationToken.None);
            await pipe.Writer.CompleteAsync();
            System.IO.Pipelines.ReadResult read = await pipe.Reader.ReadAsync();
            string json = System.Text.Encoding.UTF8.GetString(System.Buffers.BuffersExtensions.ToArray(read.Buffer));
            JObject parsed = JObject.Parse(json);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(counter.Calls, Is.EqualTo(invalidTimeout ? 1 : 2), "prefix once, target only for valid timeout, no suffix");
                Assert.That(executed, Is.EqualTo(invalidTimeout
                    ? new[] { transactions[0].Hash! }
                    : new[] { transactions[0].Hash!, transactions[1].Hash! }));
                Assert.That(parsed["id"]!.Value<int>(), Is.EqualTo(67));
                if (scenario == "prefix-error")
                {
                    Assert.That(parsed["error"], Is.Null);
                    Assert.That(parsed["result"]?["failed"]?.Value<bool>(), Is.True);
                    Assert.That(parsed["result"]?["error"]?.Value<string>(), Does.Contain("prefix failure"));
                    Assert.That(parsed["result"]?["errorCode"]?.Value<int>(), Is.EqualTo(-32603));
                }
                else if (invalidTimeout)
                {
                    Assert.That(parsed["result"], Is.Null);
                    Assert.That(parsed["error"]?["code"]?.Value<int>(), Is.EqualTo(-32000));
                    Assert.That(parsed["error"]?["message"]?.Value<string>(), Does.Contain("Cannot parse 'bad'"));
                }
                else
                {
                    Assert.That(parsed["error"], Is.Null);
                    Assert.That(parsed["result"]?.ToString(Newtonsoft.Json.Formatting.None),
                        Is.EqualTo("{\"structLogs\":[],\"gas\":21000,\"failed\":false,\"returnValue\":\"0x\"}"));
                }
            }
        }
        finally
        {
            await pipe.Writer.CompleteAsync();
            await pipe.Reader.CompleteAsync();
        }
    }

    private static readonly string[] TraceTransactionSemanticTimeoutScenarios =
    [
        "unknown", "native", "config", "config-valid-timeout",
        "native-valid-timeout", "type", "oversized", "extension"
    ];

    [Test]
    public async Task Debug_traceTransaction_semantic_timeout_phase(
        [ValueSource(nameof(TraceTransactionSemanticTimeoutScenarios))] string scenario)
    {
        using Context context = await Context.Create();
        Transaction transaction = await AddBlockWithTransfer(context);
        bool invalidConfig = scenario is "config" or "config-valid-timeout";
        object timeout = scenario switch
        {
            "type" => 7,
            "oversized" => new string('x', 65),
            "config-valid-timeout" or "native-valid-timeout" => "5s",
            _ => "bad"
        };
        Dictionary<string, object> options = new()
        {
            ["tracer"] = invalidConfig ? "prestateTracer" : NativeNoopTracer.NoopTracer,
            ["timeout"] = timeout
        };
        if (invalidConfig) options["tracerConfig"] = new { diffMode = Array.Empty<int>() };
        Hash256 hash = scenario == "unknown" ? TestItem.KeccakD : transaction.Hash!;
        string method = scenario == "extension" ? "debug_traceTransactionInBlockByHash" : "debug_traceTransaction";
        object[] parameters = scenario == "extension"
            ? [Rlp.Encode(context.Blockchain.BlockTree.Head!).ToString(), hash, options]
            : [hash, options];
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, parameters);
        JToken json = JToken.Parse(response);
        if (scenario == "native-valid-timeout")
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(json["error"], Is.Null, response);
                Assert.That(json["result"], Is.EqualTo(new JObject()).Using(JToken.EqualityComparer), response);
            }
            return;
        }
        int expectedCode = scenario switch
        {
            "unknown" => ErrorCodes.ResourceNotFound,
            "native" or "config" or "config-valid-timeout" => ErrorCodes.InvalidInput,
            _ => ErrorCodes.InvalidParams
        };
        string? diagnostic = scenario switch
        {
            "unknown" => "transaction not found",
            "native" => "Cannot parse 'bad' as a duration",
            "config" or "config-valid-timeout" => "Cannot convert",
            _ => null
        };
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int?)json["error"]?["code"], Is.EqualTo(expectedCode), response);
            Assert.That(json["result"], Is.Null, response);
            if (diagnostic is not null) Assert.That(json["error"]?.ToString(), Does.Contain(diagnostic), response);
        }
    }

    [Test]
    public async Task Debug_traceTransaction_timeout_wire_contract(
        [Values("bad", "type", "oversized", "5s", "0s", "-1s", "4294968s")] string duration)
    {
        using Context context = await Context.Create();
        Transaction transaction = await AddBlockWithTransfer(context);
        object timeout = duration switch
        {
            "type" => 7,
            "oversized" => new string('x', 65),
            _ => duration
        };

        using (Assert.EnterMultipleScope())
        {
            foreach (bool? streamMode in new bool?[] { null, false, true })
                foreach (string? tracer in new[] { null, "noopTracer" })
                    foreach (bool known in new[] { false, true })
                    {
                        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransaction",
                            known ? transaction.Hash! : TestItem.KeccakD, new { timeout, streamMode, tracer });
                        string expected = duration is "type" or "oversized"
                            ? """{"jsonrpc":"2.0","error":{"code":-32602,"message":"Invalid params"},"id":67}"""
                            : !known
                                ? """{"jsonrpc":"2.0","error":{"code":-32000,"message":"transaction not found"},"id":67}"""
                                : duration == "bad"
                                    ? """{"jsonrpc":"2.0","error":{"code":-32000,"message":"Cannot parse 'bad' as a duration. Expected Go format ('5s', '100ms', '2m30s') or TimeSpan format ('hh:mm:ss')."},"id":67}"""
                                    : tracer is null
                                        ? """{"jsonrpc":"2.0","result":{"structLogs":[],"gas":21000,"failed":false,"returnValue":"0x"},"id":67}"""
                                        : """{"jsonrpc":"2.0","result":{},"id":67}""";

                        Assert.That(JToken.Parse(response), Is.EqualTo(JToken.Parse(expected)).Using(JToken.EqualityComparer),
                            $"streamMode={streamMode}, tracer={tracer}, known={known}");
                    }
        }
    }

    [Test]
    public async Task Debug_traceTransaction_javascript_duration_policy(
        [Values("bad", "5s", "0s", "-1s", "4294968s")] string timeout,
        [Values(null, false, true)] bool? streamMode)
    {
        using Context context = await Context.Create();
        Transaction transaction = await AddBlockWithTransfer(context);
        const string tracer = "{fault:function(){},result:function(){return {};}}";
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransaction",
            transaction.Hash!, new { tracer, timeout, streamMode });
        string expected;
        if (timeout == "bad")
        {
            expected = """{"jsonrpc":"2.0","error":{"code":-32000,"message":"Cannot parse 'bad' as a duration. Expected Go format ('5s', '100ms', '2m30s') or TimeSpan format ('hh:mm:ss')."},"id":67}""";
        }
        else if (timeout == "5s")
        {
            expected = """{"jsonrpc":"2.0","result":{},"id":67}""";
        }
        else
        {
            TimeSpan duration = TimeSpan.FromSeconds(timeout == "0s" ? 0 : timeout == "-1s" ? -1 : 4_294_968);
            ArgumentOutOfRangeException error = new("options", duration, "Tracer timeout must be between 1ns and 2m.");
            expected = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                error = new { code = ErrorCodes.InvalidParams, message = error.Message, data = $"{error.GetType()}: {error.Message}" },
                id = 67
            });
        }

        Assert.That(JToken.Parse(response), Is.EqualTo(JToken.Parse(expected)).Using(JToken.EqualityComparer));
    }

    [Test]
    public async Task Debug_traceTransaction_javascript_constructor_precedes_duration(
        [Values("callbacks", "setup")] string failure,
        [Values(null, false, true)] bool? streamMode)
    {
        using Context context = await Context.Create();
        Transaction transaction = await AddBlockWithTransfer(context);
        string tracer = failure == "callbacks" ? "{}"
            : "{fault:function(){},setup:function(){throw Error('setup-before-timeout');},result:function(){return {};}}";
        string validDuration = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransaction",
            transaction.Hash!, new { tracer, timeout = "5s", streamMode });
        string badDuration = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransaction",
            transaction.Hash!, new { tracer, timeout = "bad", streamMode });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(JToken.Parse(validDuration)["error"], Is.Not.Null, validDuration);
            Assert.That(badDuration, Is.EqualTo(validDuration));
        }
    }

    [TestCase(null, "{}")]
    [TestCase("missingTracer", "{}")]
    [TestCase("prestateTracer", "{\"diffMode\":[]}")]
    public async Task Debug_traceTransaction_unknown_hash_returns_geth_error_before_tracer_validation(string? tracer, string configuration)
    {
        using Context context = await Context.Create();
        using JsonDocument config = JsonDocument.Parse(configuration);
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransaction",
            TestItem.KeccakA, new { tracer, tracerConfig = config.RootElement, timeout = "5s" });

        Assert.That(JToken.Parse(response), Is.EqualTo(JToken.Parse(
            """{"jsonrpc":"2.0","error":{"code":-32000,"message":"transaction not found"},"id":67}"""))
            .Using(JToken.EqualityComparer));
    }

    [TestCase("{\"diffMode\":[]}", true)]
    [TestCase("{}", false)]
    public async Task Debug_traceTransaction_native_config_errors_are_execution_input_errors(string configuration, bool invalid)
    {
        using Context context = await Context.Create();
        Transaction transaction = await AddBlockWithTransfer(context);
        using JsonDocument config = JsonDocument.Parse(configuration);
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransaction", transaction.Hash!,
            new { tracer = "prestateTracer", tracerConfig = config.RootElement, timeout = "5s" });
        JToken json = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            if (invalid)
            {
                Assert.That((int?)json["error"]?["code"], Is.EqualTo(ErrorCodes.InvalidInput), response);
                Assert.That((string?)json["error"]?["message"], Does.Contain("Cannot convert"), response);
                Assert.That(json["result"], Is.Null, response);
            }
            else
            {
                Assert.That(json["error"], Is.Null, response);
                Assert.That(json["result"], Is.TypeOf<JObject>(), response);
                Assert.That(json["result"]?[transaction.SenderAddress!.ToString()], Is.Not.Null, response);
            }
        }
    }

    [TestCase("json", ErrorCodes.InvalidParams)]
    [TestCase("format", ErrorCodes.InternalError)]
    [TestCase("cancellation", ErrorCodes.Timeout)]
    [NonParallelizable]
    public async Task Debug_traceTransaction_native_host_errors_keep_existing_classification(string failureKind, int expectedError)
    {
        string tracer = $"{nameof(Debug_traceTransaction_native_host_errors_keep_existing_classification)}-{failureKind}-{Guid.NewGuid():N}";
        Exception failure = failureKind switch
        {
            "json" => new JsonException("host construction failure"),
            "format" => new FormatException("host construction failure"),
            _ => new OperationCanceledException("host construction failure")
        };
        GethLikeNativeTracerFactory.RegisterTracer(tracer, (_, _, _, _) => throw failure);
        using Context context = await Context.Create();
        Transaction transaction = await AddBlockWithTransfer(context);
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransaction", transaction.Hash!,
            new { tracer, timeout = "5s" });
        JToken json = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int?)json["error"]?["code"], Is.EqualTo(expectedError), response);
            Assert.That(json["result"], Is.Null, response);
        }
    }

    [TestCaseSource(nameof(TraceTransactionTransferSource))]
    [TestCaseSource(nameof(TraceTransactionContractSource))]
    public async Task Debug_traceTransaction(Func<TestRpcBlockchain, Transaction> factory, GethTraceOptions options, string expected)
    {
        using Context context = await Context.Create();

        Transaction transaction = factory(context.Blockchain);
        await context.Blockchain.AddBlock(transaction);

        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransaction", transaction.Hash, options);

        Assert.That(JToken.Parse(response), Is.EqualTo(JToken.Parse(expected)).Using(JToken.EqualityComparer));
    }

    [Test]
    public async Task Debug_traceTransaction_with_block_override_does_not_mutate_cached_header()
    {
        using Context context = await Context.Create();

        Transaction transaction = await AddBlockWithTransfer(context);

        BlockHeader header = context.Blockchain.BlockTree.Head!.Header;
        UInt256 baseFee = header.BaseFeePerGas;

        GethTraceOptions options = new()
        {
            BlockOverrides = new BlockOverride { BaseFeePerGas = baseFee + UInt256.One }
        };
        await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransaction", transaction.Hash, options);

        Assert.That(header.BaseFeePerGas, Is.EqualTo(baseFee), "block override must not write into the block-tree-cached header");
    }

    // Zero is the case where the overridden target would pass for genesis and open pre-genesis state; a cold
    // header cache is the case where the parent lookup keys on the overridden number and misses the store.
    [Test]
    public async Task Debug_traceTransaction_with_block_number_override_traces_against_the_original_parent_state([Values(0ul, 1000ul)] ulong numberOverride)
    {
        using Context context = await Context.Create();

        Transaction transaction = await AddBlockWithTransfer(context);
        ((IClearableCache)context.Blockchain.Container.Resolve<IHeaderStore>()).ClearCache();

        GethTraceOptions options = new() { BlockOverrides = new BlockOverride { Number = numberOverride } };
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransaction", transaction.Hash, options);

        JToken result = JToken.Parse(response)["result"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result["failed"]?.Value<bool>(), Is.False, response);
            Assert.That(result["gas"]?.Value<ulong>(), Is.EqualTo(GasCostOf.Transaction));
        }
    }

    [Test]
    public async Task Debug_traceTransaction_callTracer_log_index_continues_from_preceding_transactions()
    {
        using Context context = await Context.Create();

        ulong nonce = context.Blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Transaction first = Build.A.Transaction
            .WithNonce(nonce)
            .WithCode(Prepare.EvmCode.Log(0, 0).Log(0, 0).STOP().Done)
            .WithGasLimit(100000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        Transaction second = Build.A.Transaction
            .WithNonce(nonce + 1)
            .WithCode(Prepare.EvmCode.Log(0, 0).STOP().Done)
            .WithGasLimit(100000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        await context.Blockchain.AddBlock(first, second);

        GethTraceOptions options = new()
        {
            Tracer = NativeCallTracer.CallTracer,
            TracerConfig = JsonSerializer.Deserialize<JsonElement>("""{"withLog":true}""")
        };
        string trace = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransaction", second.Hash, options);
        string blockTrace = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByNumber", context.Blockchain.BlockTree.Head!.Number, options);
        string receipt = await RpcTest.TestSerializedRequest(context.Blockchain.EthRpcModule, "eth_getTransactionReceipt", second.Hash);

        JToken traceLog = JToken.Parse(trace)["result"]!["logs"]!.Single();
        JToken blockTraceLog = JToken.Parse(blockTrace)["result"]![1]!["result"]!["logs"]!.Single();
        JToken receiptLog = JToken.Parse(receipt)["result"]!["logs"]!.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((string)receiptLog["logIndex"]!, Is.EqualTo("0x2"));
            Assert.That((string)traceLog["index"]!, Is.EqualTo("0x2"));
            Assert.That((string)blockTraceLog["index"]!, Is.EqualTo("0x2"));
        }
    }

    [TestCaseSource(nameof(TraceTransactionTransferSource))]
    [TestCaseSource(nameof(TraceTransactionContractSource))]
    public async Task Debug_traceTransactionByBlockAndIndex(Func<TestRpcBlockchain, Transaction> factory, GethTraceOptions options, string expected)
    {
        using Context context = await Context.Create();

        Transaction transaction = factory(context.Blockchain);
        await context.Blockchain.AddBlock(transaction);

        ulong blockNumber = context.Blockchain.BlockTree.Head!.Number;
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransactionByBlockAndIndex", blockNumber, "0x0", options);

        Assert.That(JToken.Parse(response), Is.EqualTo(JToken.Parse(expected)).Using(JToken.EqualityComparer));
    }

    [TestCase("latest")]
    [TestCase("pending")]
    [TestCase(null, TestName = "Debug_traceTransactionByBlockAndIndex_accepts_block_hash")]
    public async Task Debug_traceTransactionByBlockAndIndex_accepts_block_tag_or_hash(string? blockTag)
    {
        using Context context = await Context.Create();

        await AddBlockWithTransfer(context);

        Block head = context.Blockchain.BlockTree.Head!;
        // A hash cannot be a constant TestCase argument, so a null tag stands for the head's hash
        object blockParameter = blockTag ?? (object)head.Hash!;
        string expected = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransactionByBlockAndIndex", head.Number, "0x0");
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransactionByBlockAndIndex", blockParameter, "0x0");

        JToken expectedToken = JToken.Parse(expected);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(expectedToken["result"]?["failed"]?.Value<bool>(), Is.False, "the numeric baseline must actually trace, otherwise the comparison below is vacuous");
            Assert.That(JToken.Parse(response), Is.EqualTo(expectedToken).Using(JToken.EqualityComparer));
        }
    }

    [Test]
    public async Task Debug_traceTransactionByBlockAndIndex_does_not_fall_back_to_the_canonical_block()
    {
        using Context context = await Context.Create();

        await AddBlockWithTransfer(context);
        Block canonical = context.Blockchain.BlockTree.Head!;
        BlockHeader parent = context.Blockchain.BlockTree.FindHeader(canonical.ParentHash!, BlockTreeLookupOptions.None)!;

        Block sideChain = Build.A.Block
            .WithParent(parent)
            .WithStateRoot(TestItem.KeccakA)
            .WithExtraData([1])
            .TestObject;
        AddBlockResult suggested = context.Blockchain.BlockTree.SuggestBlock(sideChain, BlockTreeSuggestOptions.ForceDontSetAsMain);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(suggested, Is.EqualTo(AddBlockResult.Added), "the sibling must actually reach the block tree for this test to mean anything");
            Assert.That(context.Blockchain.BlockTree.FindBlock(canonical.Number, BlockTreeLookupOptions.RequireCanonical)!.Hash,
                Is.EqualTo(canonical.Hash), "the suggested sibling must stay off the canonical chain for this test to mean anything");
        }

        string byNumber = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransactionByBlockAndIndex", canonical.Number, "0x0");
        string byHash = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransactionByBlockAndIndex", sideChain.Hash!, "0x0");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(JToken.Parse(byNumber)["result"]?["failed"]?.Value<bool>(), Is.False, "the canonical block at that height does have a transaction to trace");
            AssertTracerWasReached(byHash, "has only 0 transactions",
                "a non-canonical hash must not silently trace the canonical block at the same height");
        }
    }

    [Test]
    public async Task Debug_traceTransactionByBlockAndIndex_rejects_a_negative_index()
    {
        using Context context = await Context.Create();

        await AddBlockWithTransfer(context);
        Block head = context.Blockchain.BlockTree.Head!;

        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransactionByBlockAndIndex", head.Number, -1);

        AssertTracerWasReached(response, $"has only {head.Transactions.Length} transactions and the requested tx index was -1",
            "a negative index must be reported by the bounds check, not indexed into the transaction array");
    }

    [Test]
    public async Task Debug_traceTransactionByBlockAndIndex_needs_no_state_for_genesis()
    {
        using Context context = await Context.Create();

        await AddBlockWithTransfer(context);

        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransactionByBlockAndIndex", 0UL, "0x0");

        AssertTracerWasReached(response, "has only 0 transactions",
            "genesis has no parent to replay from, so no base-state check applies and the request must not be rejected");
    }

    /// <summary>
    /// Asserts that a trace request reached transaction tracing rather than being rejected by the base-state
    /// guard, and that the resulting error contains <paramref name="expectedErrorSubstring"/>.
    /// </summary>
    /// <remarks>
    /// A response rejected by the guard has no "result" node, so <c>["result"]?["error"]</c> silently
    /// evaluates to <see langword="null"/> and handing that to <see cref="Does.Contain"/> throws
    /// <see cref="ArgumentException"/> instead of failing the assertion - checking for null first keeps
    /// <paramref name="because"/> reaching the test report.
    /// </remarks>
    private static void AssertTracerWasReached(string response, string expectedErrorSubstring, string because)
    {
        string? error = JToken.Parse(response)["result"]?["error"]?.Value<string>();

        Assert.That(error, Is.Not.Null, because);
        if (error is not null)
        {
            Assert.That(error, Does.Contain(expectedErrorSubstring), because);
        }
    }

    // A streamed trace reports a failure inside `result`, so only non-empty structLogs prove that the trace ran.
    [Test]
    public async Task Debug_traceTransaction_without_options_streams_struct_logs()
    {
        using Context context = await Context.Create();
        Block block = await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));

        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransaction", block.Transactions[^1].Hash!);

        AssertStructLogsTraced(JToken.Parse(response)["result"]!, response);
    }

    private static void AssertStructLogsTraced(JToken trace, string response)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(trace["error"], Is.Null, response);
            Assert.That((JArray?)trace["structLogs"], Is.Not.Null.And.Not.Empty, response);
        }
    }

    private static async Task<Transaction> AddBlockWithTransfer(Context context)
    {
        Transaction transaction = Build.A.Transaction
            .WithNonce(context.Blockchain.ReadOnlyState.GetNonce(TestItem.AddressA))
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        await context.Blockchain.AddBlock(transaction);
        return transaction;
    }

    [TestCaseSource(nameof(TraceTransactionTransferSource))]
    [TestCaseSource(nameof(TraceTransactionContractSource))]
    public async Task Debug_traceTransactionByBlockhashAndIndex(Func<TestRpcBlockchain, Transaction> factory, GethTraceOptions options, string expected)
    {
        using Context context = await Context.Create();

        Transaction transaction = factory(context.Blockchain);
        await context.Blockchain.AddBlock(transaction);

        Hash256? blockHash = context.Blockchain.BlockTree.Head!.Hash;
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransactionByBlockhashAndIndex", blockHash, "0x0", options);

        Assert.That(JToken.Parse(response), Is.EqualTo(JToken.Parse(expected)).Using(JToken.EqualityComparer));
    }

    [Test]
    public async Task Debug_traceTransactionByBlockhashAndIndex_traces_a_block_whose_own_state_was_never_committed()
    {
        using Context context = await Context.Create();

        await AddBlockWithTransfer(context);
        BlockHeader parent = context.Blockchain.BlockTree.Head!.Header;

        // A root never committed anywhere, so HasStateForBlock fails under both backends.
        Block unprocessed = Build.A.Block
            .WithParent(parent)
            .WithStateRoot(TestItem.KeccakA)
            .TestObject;
        context.Blockchain.BlockTree.SuggestBlock(unprocessed, BlockTreeSuggestOptions.ForceDontSetAsMain);

        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransactionByBlockhashAndIndex", unprocessed.Hash!, "0x0");

        AssertTracerWasReached(response, "has only 0 transactions",
            "the trace replays the block on top of its parent, so the block's own state is not a precondition");
    }

    [TestCaseSource(nameof(TraceTransactionTransferSource))]
    [TestCaseSource(nameof(TraceTransactionContractSource))]
    public async Task Debug_traceTransactionInBlockByHash(Func<TestRpcBlockchain, Transaction> factory, GethTraceOptions options, string expected)
    {
        using Context context = await Context.Create();

        Transaction transaction = factory(context.Blockchain);
        await context.Blockchain.AddBlock(transaction);

        string blockRlp = Rlp.Encode(context.Blockchain.BlockTree.Head!).ToString();
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransactionInBlockByHash", blockRlp, transaction.Hash, options);

        Assert.That(JToken.Parse(response), Is.EqualTo(JToken.Parse(expected)).Using(JToken.EqualityComparer));
    }

    [TestCaseSource(nameof(TraceTransactionTransferSource))]
    [TestCaseSource(nameof(TraceTransactionContractSource))]
    public async Task Debug_traceTransactionInBlockByIndex(Func<TestRpcBlockchain, Transaction> factory, GethTraceOptions options, string expected)
    {
        using Context context = await Context.Create();

        Transaction transaction = factory(context.Blockchain);
        await context.Blockchain.AddBlock(transaction);

        string blockRlp = Rlp.Encode(context.Blockchain.BlockTree.Head!).ToString();
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransactionInBlockByIndex", blockRlp, "0x0", options);

        Assert.That(JToken.Parse(response), Is.EqualTo(JToken.Parse(expected)).Using(JToken.EqualityComparer));
    }

    private static IEnumerable<TestCaseData> TraceTransactionTransferSource()
    {
        Func<TestRpcBlockchain, Transaction> transferTransaction = (TestRpcBlockchain b) => Build.A.Transaction
            .WithNonce(b.ReadOnlyState.GetNonce(TestItem.AddressA))
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        yield return new TestCaseData(
            transferTransaction,
            new GethTraceOptions(),
            """{"jsonrpc":"2.0","result":{"gas":21000,"failed":false,"returnValue":"0x","structLogs":[]},"id":67}"""
        )
        { TestName = "Transfer with blockMemoryTracer" };

        yield return new TestCaseData(
            transferTransaction,
            new GethTraceOptions { Tracer = "{gasUsed: [], step: function(log) { this.gasUsed.push(log.getGas()); }, result: function() { return this.gasUsed; }, fault: function(){}}" },
            """{"jsonrpc":"2.0","result":[],"id":67}"""
        )
        { TestName = "Transfer with javaScriptTracer" };

        yield return new TestCaseData(
            transferTransaction,
            new GethTraceOptions { Tracer = Native4ByteTracer.FourByteTracer },
            """{"jsonrpc":"2.0","result":{},"id":67}"""
        )
        { TestName = "Transfer with " + Native4ByteTracer.FourByteTracer };

        yield return new TestCaseData(
            transferTransaction,
            new GethTraceOptions { Tracer = NativeNoopTracer.NoopTracer },
            """{"jsonrpc":"2.0","result":{},"id":67}"""
        )
        { TestName = "Transfer with " + NativeNoopTracer.NoopTracer };

        yield return new TestCaseData(
            transferTransaction,
            new GethTraceOptions { Tracer = NativeCallTracer.CallTracer },
            """
            {
                "jsonrpc": "2.0",
                "result": {
                    "type": "CALL",
                    "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
                    "to": "0x0000000000000000000000000000000000000000",
                    "value": "0x1",
                    "gas": "0x5208",
                    "gasUsed": "0x5208",
                    "input": "0x"
                },
                "id": 67
            }
            """
        )
        { TestName = "Transfer with " + NativeCallTracer.CallTracer };

        yield return new TestCaseData(
            transferTransaction,
            new GethTraceOptions { Tracer = NativePrestateTracer.PrestateTracer },
            """
            {
                "jsonrpc": "2.0",
                "result": {
                    "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
                        "balance": "0x3635c9adc5de9f09e5",
                        "nonce": 3,
                        "code": "0xabcd",
                        "codeHash": "0xdbe576b4818846aa77e82f4ed5fa78f92766b141f282d36703886d196df39322"
                    },
                    "0x475674cb523a0a2736b7f7534390288fce16982c": {
                        "balance": "0xf618"
                    }
                },
                "id": 67
            }
            """
        )
        { TestName = "Transfer with " + NativePrestateTracer.PrestateTracer };
    }

    private static IEnumerable<TestCaseData> TraceTransactionContractSource()
    {
        byte[] code = Prepare.EvmCode
            .PushData(0)
            .PushData(32)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        Func<TestRpcBlockchain, Transaction> contractTransaction = (TestRpcBlockchain b) => Build.A.Transaction
            .WithNonce(b.ReadOnlyState.GetNonce(TestItem.AddressA))
            .WithCode(code)
            .WithGasLimit(100000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        yield return new TestCaseData(
            contractTransaction,
            new GethTraceOptions(),
            """
            {
                "jsonrpc": "2.0",
                "result": {
                    "gas": 55278,
                    "failed": false,
                    "returnValue": "0x",
                    "structLogs": [
                        {
                            "pc": 0,
                            "op": "PUSH1",
                            "gas": 46928,
                            "gasCost": 3,
                            "depth": 1,
                            "stack": []
                        },
                        {
                            "pc": 2,
                            "op": "PUSH1",
                            "gas": 46925,
                            "gasCost": 3,
                            "depth": 1,
                            "stack": [
                                "0x0"
                            ]
                        },
                        {
                            "pc": 4,
                            "op": "SSTORE",
                            "gas": 46922,
                            "gasCost": 2200,
                            "depth": 1,
                            "stack": [
                                "0x0",
                                "0x20"
                            ],
                            "storage": {
                                "0x0000000000000000000000000000000000000000000000000000000000000020": "0x0000000000000000000000000000000000000000000000000000000000000000"
                            }
                        },
                        {
                            "pc": 5,
                            "op": "STOP",
                            "gas": 44722,
                            "gasCost": 0,
                            "depth": 1,
                            "stack": []
                        }
                    ]
                },
                "id": 67
            }
            """
        )
        { TestName = "Contract with blockMemoryTracer" };

        yield return new TestCaseData(
            contractTransaction,
            new GethTraceOptions { Tracer = "{gasUsed: [], step: function(log) { this.gasUsed.push(log.getGas()); }, result: function() { return this.gasUsed; }, fault: function(){}}" },
            """{"jsonrpc":"2.0","result":[46928,46925,46922,44722],"id":67}"""
        )
        { TestName = "Contract with javaScriptTracer" };

        yield return new TestCaseData(
            contractTransaction,
            new GethTraceOptions { Tracer = Native4ByteTracer.FourByteTracer },
            """{"jsonrpc":"2.0","result":{},"id":67}"""
        )
        { TestName = "Contract with " + Native4ByteTracer.FourByteTracer };

        yield return new TestCaseData(
            contractTransaction,
            new GethTraceOptions { Tracer = NativeCallTracer.CallTracer },
            """
            {
                "jsonrpc": "2.0",
                "result": {
                    "type": "CREATE",
                    "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
                    "to": "0x0ffd3e46594919c04bcfd4e146203c8255670828",
                    "value": "0x1",
                    "gas": "0x186a0",
                    "gasUsed": "0xd7ee",
                    "input": "0x600060205500"
                },
                "id": 67
            }
            """
        )
        { TestName = "Contract with " + NativeCallTracer.CallTracer };

        yield return new TestCaseData(
            contractTransaction,
            new GethTraceOptions { Tracer = NativePrestateTracer.PrestateTracer },
            """
            {
                "jsonrpc": "2.0",
                "result": {
                    "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
                        "balance": "0x3635c9adc5de9f09e5",
                        "nonce": 3,
                        "code": "0xabcd",
                        "codeHash": "0xdbe576b4818846aa77e82f4ed5fa78f92766b141f282d36703886d196df39322"
                    },
                    "0x475674cb523a0a2736b7f7534390288fce16982c": {
                        "balance": "0xf618"
                    }
                },
                "id": 67
            }
            """
        )
        { TestName = "Contract with " + NativePrestateTracer.PrestateTracer };
    }
}
