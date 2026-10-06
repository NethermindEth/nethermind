// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Tracing;
using Nethermind.Blockchain.Find;
using Nethermind.JsonRpc.Modules;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.FourByte;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Noop;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Prestate;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    [TestCase(null, false)]
    [TestCase("callTracer", false)]
    [TestCase("{fault:function(){},result:function(ctx){return {index:ctx.txIndex,gas:ctx.gasUsed};}}", false)]
    [TestCase("{fault:function(){},result:function(ctx){if(ctx.txIndex==1)throw new Error('chain failure');return {index:ctx.txIndex};}}", true)]
    [TestCase("noopTracer", false, "9223372036854775807ns")]
    [TestCase("noopTracer", true, "0", "execution timeout")]
    [TestCase("{fault:function(){},result:function(){return {};}}", true, "0", "execution timeout")]
    [TestCase("callTracer", true, "bad", "time: invalid duration \"bad\"")]
    [TestCase("noopTracer", true, "bad", "time: invalid duration \"bad\"")]
    [TestCase("noopTracer", true, "1", "time: missing unit in duration \"1\"")]
    [TestCase("{fault:function(){}}", true, "bad", "trace object must expose a function result()")]
    [TestCase("{result:function(){return {};}}", true, "bad", "trace object must expose a function fault()")]
    public async Task TraceChain_real_block_matches_block_trace(string? tracer, bool tracerError, string? timeout = null, string? firstError = null)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = false }).Build();
        ulong first = chain.BlockTree.Head!.Number;
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(chain.BlockTree.Head.Header, TestItem.AddressB);
        Transaction[] transactions = Enumerable.Range(0, 3).Select(index => Build.A.Transaction
            .WithCode(Prepare.EvmCode.PushData(index).PushData(0).Op(Instruction.MSTORE).Return(32, 0).Done)
            .WithNonce(nonce + (ulong)index).WithGasLimit(1_000_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject).ToArray();
        Block block = await chain.AddBlock(transactions);
        Assert.That(block.Transactions, Has.Length.EqualTo(3));
        IDebugRpcModule module = chain.DebugRpcModule;
        JToken? expected = tracerError ? null : JToken.Parse(await RpcTest.TestSerializedRequest(module, "debug_traceBlockByHash", block.Hash!, new { tracer }))["result"]!;
        if (!tracerError) Assert.That((JArray)expected!, Has.Count.EqualTo(3));
        IJsonRpcDuplexClient client = Substitute.For<IJsonRpcDuplexClient>();
        client.Id.Returns("real-chain-client");
        string? notification = null;
        client.SendJsonRpcResult(Arg.Any<JsonRpcResult>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            notification = RpcTest.SerializeResponse(call.Arg<JsonRpcResult>().Response);
            return Task.FromResult(0);
        });
        using JsonRpcContext context = new(RpcEndpoint.Ws, client);
        Assert.That(JsonRpcContext.Current.Value, Is.SameAs(context));
        using PendingTraceChainResponse response = (PendingTraceChainResponse)((IDebugSubscriptionRpcModule)module)
            .debug_subscribe("traceChain", new BlockParameter(first), new BlockParameter(block.Number), JsonSerializer.Deserialize<TraceChainOptions>(JsonSerializer.Serialize(new { tracer, timeout }), EthereumJsonSerializer.JsonOptions));
        RpcTest.ConfigureTraceChainRental(response.Subscription, module);
        try
        {
            response.TakeActivation().Activate();
            await response.Subscription.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(notification, Is.Not.Null);
            JToken actual = JToken.Parse(notification!)["params"]!["result"]!["traces"]!;
            if (tracerError)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That((JArray)actual, Has.Count.EqualTo(3));
                    if (firstError is not null)
                    {
                        Assert.That((string?)actual[0]?["error"], Is.EqualTo(firstError), notification);
                        Assert.That(actual[1]!.Type, Is.EqualTo(JTokenType.Null), notification);
                    }
                    else
                    {
                        Assert.That((int?)actual[0]?["result"]?["index"], Is.Zero, notification);
                        Assert.That((string?)actual[1]?["error"], Does.Contain("chain failure"), notification);
                    }
                    Assert.That(actual[2]!.Type, Is.EqualTo(JTokenType.Null), notification);
                }
            }
            else Assert.That(JToken.DeepEquals(actual, expected), Is.True, notification);
        }
        finally
        {
            response.Subscription.Abort();
        }
    }

    [Test]
    public async Task Debug_traceBlock_SuppliedBody_DoesNotUseIndexedHeaderState()
    {
        IParallelBlockTracer parallel = Substitute.For<IParallelBlockTracer>();
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = false })
            .Build(builder => builder
                .AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance) { AllowTestChainOverride = false })
                .AddSingleton(parallel));
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressB);
        Transaction[] transactions = new Transaction[3];
        for (int i = 0; i < transactions.Length; i++)
            transactions[i] = Build.A.Transaction.WithTo(TestItem.AddressC).WithNonce(nonce + (ulong)i)
                .WithValue(1).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block original = await chain.AddBlock(transactions);
        GethTraceOptions options = new() { Tracer = "prestateTracer" };
        string baseline = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlockByHash", original.Hash, options);
        Assert.That(parallel.ReceivedCalls(), Is.Not.Empty, "the RPC tracer must be wired to the indexed path for verified blocks");
        parallel.ClearReceivedCalls();
        Transaction[] changed = (Transaction[])original.Transactions.Clone();
        changed[0] = Build.A.Transaction.WithTo(TestItem.AddressC).WithNonce(nonce)
            .WithValue(100).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block supplied = new(original.Header.Clone(), changed, original.Uncles, original.Withdrawals);

        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlock", Rlp.Encode(supplied).ToString(), options);
        JToken expected = JToken.Parse(baseline);
        JToken actual = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual["error"], Is.Null);
            Assert.That(parallel.ReceivedCalls(), Is.Empty, "an unverified body must never open indexed prefixes");
            Assert.That(supplied.Hash, Is.EqualTo(original.Hash));
        }
        string sender = TestItem.AddressB.ToString();
        UInt256 before = Bytes.FromHexString(expected["result"]![1]!["result"]![sender]!["balance"]!.Value<string>()!).ToUInt256();
        UInt256 after = Bytes.FromHexString(actual["result"]![1]!["result"]![sender]!["balance"]!.Value<string>()!).ToUInt256();
        Assert.That(after, Is.EqualTo(before - 99), "the second transaction must see the modified first transfer, not the indexed prefix");
    }

    [TestCase("debug_traceBlockByHash")]
    [TestCase("debug_traceBlockByNumber")]
    public async Task Debug_traceBlock_preimage_results_preserve_transaction_hash(string method)
    {
        using Context context = await Context.Create();
        ulong nonce = context.Blockchain.WorldStateManager.GlobalStateReader.GetNonce(context.Blockchain.BlockTree.Head!.Header, TestItem.AddressB);
        Transaction transaction = Build.A.Transaction.WithTo(TestItem.AddressC).WithNonce(nonce)
            .WithValue(1).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block block = await context.Blockchain.AddBlock(transaction);
        object selector = method == "debug_traceBlockByHash" ? block.Hash! : block.Number;
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, selector,
            new { tracer = "keccak256PreimageTracer" });
        JToken result = JToken.Parse(response);
        Assert.That(result["error"], Is.Null, response);
        Assert.That(result["result"]!.Count(), Is.EqualTo(block.Transactions.Length));
        for (int i = 0; i < block.Transactions.Length; i++)
            Assert.That(result["result"]![i]!["txHash"]!.Value<string>(), Is.EqualTo(block.Transactions[i].Hash!.ToString()));
    }

    // The supplied body keeps the stored header's hash, and the new header has no stored receipts: both must number
    // logs from the body that runs, not from receipts looked up by hash.
    [TestCase(false, false, false, TestName = "Debug_traceBlock_callTracer_log_index_follows_supplied_body_under_stored_hash")]
    [TestCase(true, false, false, TestName = "Debug_traceBlock_callTracer_log_index_follows_supplied_body_under_unstored_hash")]
    [TestCase(true, true, false, TestName = "Debug_traceBlock_callTracer_log_index_follows_supplied_body_after_reverted_tx")]
    [TestCase(false, false, true, TestName = "Debug_traceBlock_callTracer_log_index_follows_supplied_body_filtered_to_one_tx")]
    public async Task Debug_traceBlock_callTracer_log_index_follows_supplied_body(bool unstoredHeader, bool revertFirst, bool filterToSecond)
    {
        using Context context = await Context.Create();

        ulong nonce = context.Blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        static Transaction LoggingTransaction(ulong txNonce, int logs, bool revert = false)
        {
            Prepare code = Prepare.EvmCode;
            for (int i = 0; i < logs; i++) code = code.Log(0, 0);
            code = revert ? code.Revert(0, 0) : code.STOP();
            return Build.A.Transaction.WithNonce(txNonce).WithCode(code.Done).WithGasLimit(100000)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        }

        Block original = await context.Blockchain.AddBlock(LoggingTransaction(nonce, 1), LoggingTransaction(nonce + 1, 1));
        BlockHeader header = original.Header.Clone();
        if (unstoredHeader)
        {
            header.ExtraData = [0x01];
            header.Hash = header.CalculateHash();
        }
        Block supplied = new(header, [LoggingTransaction(nonce, 2, revertFirst), original.Transactions[1]], original.Uncles, original.Withdrawals);

        GethTraceOptions options = new()
        {
            Tracer = NativeCallTracer.CallTracer,
            TracerConfig = JsonSerializer.Deserialize<JsonElement>("""{"withLog":true}"""),
            TxHash = filterToSecond ? supplied.Transactions[1].Hash : null
        };
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlock", Rlp.Encode(supplied).ToString(), options);
        if (filterToSecond)
        {
            string single = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransactionInBlockByHash",
                Rlp.Encode(supplied).ToString(), supplied.Transactions[1].Hash, options with { TxHash = null });
            Assert.That((string)JToken.Parse(single)["result"]!["logs"]![0]!["index"]!, Is.EqualTo("0x2"), single);
        }

        JToken result = JToken.Parse(response)["result"]!;
        if (filterToSecond)
        {
            Assert.That((string)result.Single()["result"]!["logs"]![0]!["index"]!, Is.EqualTo("0x2"), response);
            return;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result[0]!["result"]!["logs"]?.Select(static log => (string)log["index"]!), revertFirst ? Is.Null : Is.EqualTo(new[] { "0x0", "0x1" }), response);
            Assert.That((string)result[1]!["result"]!["logs"]![0]!["index"]!, Is.EqualTo(revertFirst ? "0x0" : "0x2"), response);
        }
    }

    [Test]
    public async Task Debug_traceBlock_callTracer_filtered_supplied_body_stops_after_target()
    {
        using Context context = await Context.Create();

        ulong nonce = context.Blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Transaction target = Build.A.Transaction.WithNonce(nonce).WithCode(Prepare.EvmCode.Log(0, 0).STOP().Done).WithGasLimit(100000)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block original = await context.Blockchain.AddBlock(target);
        Transaction belowIntrinsicGas = Build.A.Transaction.WithNonce(nonce + 1).WithTo(TestItem.AddressC).WithGasLimit(1000)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block supplied = new(original.Header.Clone(), [target, belowIntrinsicGas], original.Uncles, original.Withdrawals);

        GethTraceOptions options = new()
        {
            Tracer = NativeCallTracer.CallTracer,
            TracerConfig = JsonSerializer.Deserialize<JsonElement>("""{"withLog":true}"""),
            TxHash = target.Hash
        };
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlock", Rlp.Encode(supplied).ToString(), options);

        Assert.That((string)JToken.Parse(response)["result"]!.Single()["result"]!["logs"]![0]!["index"]!, Is.EqualTo("0x0"), response);
    }

    [Test]
    public async Task Debug_traceBlock_opcode_logger_limit_resets_per_transaction([Values] bool streamMode)
    {
        using Context context = await Context.Create();
        await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        string unlimited = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByNumber",
            "latest", new { streamMode });
        string limited = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByNumber",
            "latest", new { streamMode, limit = 1 });

        JToken expected = JToken.Parse(unlimited);
        JArray transactions = (JArray)expected["result"]!;
        Assert.That(transactions, Has.Count.EqualTo(2));
        foreach (JToken transaction in transactions)
        {
            JArray entries = (JArray)transaction["result"]!["structLogs"]!;
            Assert.That(entries.Count, Is.GreaterThan(1));
            while (entries.Count > 1)
                entries.RemoveAt(entries.Count - 1);
        }

        Assert.That(JToken.DeepEquals(JToken.Parse(limited), expected), Is.True, limited);
    }

    [Test]
    public async Task Debug_traceBlock_with_invalid_rlp()
    {
        using Context context = await Context.Create();

        const string expected = """{"jsonrpc":"2.0","error":{"code":-32602,"message":"Invalid params"},"id":67}""";
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlock", "xxx");

        Assert.That(JsonElement.DeepEquals(
            JsonDocument.Parse(response).RootElement,
            JsonDocument.Parse(expected).RootElement),
            response);
    }

    [TestCaseSource(nameof(TraceBlockSource))]
    public async Task Debug_traceBlock(Func<TestRpcBlockchain, Transaction[]> factory, GethTraceOptions options, string expected)
    {
        using Context context = await Context.Create();

        await context.Blockchain.AddBlock(factory(context.Blockchain));

        string rlp = Rlp.Encode(context.Blockchain.BlockTree.Head).ToString();
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlock", rlp, options);

        Assert.That(JsonElement.DeepEquals(
                JsonDocument.Parse(response).RootElement,
                JsonDocument.Parse(expected).RootElement),
            response);
    }

    [TestCaseSource(nameof(TraceBlockSource))]
    public async Task Debug_traceBlockByNumber(Func<TestRpcBlockchain, Transaction[]> factory, GethTraceOptions options, string expected)
    {
        using Context context = await Context.Create();

        await context.Blockchain.AddBlock(factory(context.Blockchain));

        ulong blockNumber = context.Blockchain.BlockTree.Head!.Number;
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByNumber", blockNumber, options);

        Assert.That(JsonElement.DeepEquals(
                JsonDocument.Parse(response).RootElement,
                JsonDocument.Parse(expected).RootElement),
            response);
    }

    [TestCaseSource(nameof(TraceBlockSource))]
    public async Task Debug_traceBlockByHash(Func<TestRpcBlockchain, Transaction[]> factory, GethTraceOptions options, string expected)
    {
        using Context context = await Context.Create();

        await context.Blockchain.AddBlock(factory(context.Blockchain));

        Hash256? blockHash = context.Blockchain.BlockTree.Head!.Hash;
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByHash", blockHash, options);

        Assert.That(JsonElement.DeepEquals(
                JsonDocument.Parse(response).RootElement,
                JsonDocument.Parse(expected).RootElement),
            response);
    }

    [Test]
    public async Task Debug_traceBlockByHash_does_not_fail_bal_validation_when_tracing_osaka_block()
    {
        using Context context = await Context.Create(new TestSingleReleaseSpecProvider(Osaka.Instance));

        await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));

        Hash256 blockHash = context.Blockchain.BlockTree.Head!.Hash!;
        JsonRpcResponse response = await RpcTest.TestRequest(
            context.DebugRpcModule,
            "debug_traceBlockByHash",
            blockHash,
            new GethTraceOptions { Tracer = NativePrestateTracer.PrestateTracer });

        RpcTest.AssertSuccess<IReadOnlyCollection<GethLikeTxTrace>>(response);
    }

    private static IEnumerable<TestCaseData> TraceBlockSource()
    {
        Func<TestRpcBlockchain, Transaction[]> transactions = CreateTraceBlockTransactions;

        yield return new TestCaseData(
            transactions,
            new GethTraceOptions(),
            """
            {
                "jsonrpc": "2.0",
                "result": [
                    {
                        "result": {
                            "gas": 87700,
                            "failed": false,
                            "returnValue": "0x",
                            "structLogs": [
                                { "pc": 0, "op": "PUSH32", "gas": 46536, "gasCost": 3, "depth": 1, "stack": [] },
                                { "pc": 33, "op": "PUSH1", "gas": 46533, "gasCost": 3, "depth": 1, "stack": ["0x6000602055000000000000000000000000000000000000000000000000000000"] },
                                { "pc": 35, "op": "MSTORE", "gas": 46530, "gasCost": 6, "depth": 1, "stack": ["0x6000602055000000000000000000000000000000000000000000000000000000", "0x0"] },
                                { "pc": 36, "op": "PUSH32", "gas": 46524, "gasCost": 3, "depth": 1, "stack": [] },
                                { "pc": 69, "op": "PUSH1", "gas": 46521, "gasCost": 3, "depth": 1, "stack": ["0x0"] },
                                { "pc": 71, "op": "PUSH1", "gas": 46518, "gasCost": 3, "depth": 1, "stack": ["0x0", "0x6"] },
                                { "pc": 73, "op": "PUSH1", "gas": 46515, "gasCost": 3, "depth": 1, "stack": ["0x0", "0x6", "0x0"] },
                                { "pc": 75, "op": "CREATE2", "gas": 46512, "gasCost": 32006, "depth": 1, "stack": ["0x0", "0x6", "0x0", "0x0"] },
                                { "pc": 0, "op": "PUSH1", "gas": 14280, "gasCost": 3, "depth": 2, "stack": [] },
                                { "pc": 2, "op": "PUSH1", "gas": 14277, "gasCost": 3, "depth": 2, "stack": ["0x0"] },
                                { "pc": 4, "op": "SSTORE", "gas": 14274, "gasCost": 2200, "depth": 2, "stack": ["0x0", "0x20"], "storage": { "0x0000000000000000000000000000000000000000000000000000000000000020": "0x0000000000000000000000000000000000000000000000000000000000000000" } },
                                { "pc": 5, "op": "STOP", "gas": 12074, "gasCost": 0, "depth": 2, "stack": [] },
                                { "pc": 76, "op": "STOP", "gas": 12300, "gasCost": 0, "depth": 1, "stack": ["0x28156f6fdeeffd5667d51bb8d7d5069a920e0837"] }
                            ]
                        },
                        "txHash": "0xb5a78a1eda0ae98d4f62eec3e0b7f5bf81810cd57bc75006b611982667bcdbe7"
                    },
                    {
                        "result": {
                            "gas": 56141,
                            "failed": false,
                            "returnValue": "0x",
                            "structLogs": [
                                { "pc": 0, "op": "PUSH1", "gas": 46480, "gasCost": 3, "depth": 1, "stack": [] },
                                { "pc": 2, "op": "PUSH1", "gas": 46477, "gasCost": 3, "depth": 1, "stack": ["0x0"] },
                                { "pc": 4, "op": "PUSH1", "gas": 46474, "gasCost": 3, "depth": 1, "stack": ["0x0", "0x0"] },
                                { "pc": 6, "op": "PUSH1", "gas": 46471, "gasCost": 3, "depth": 1, "stack": ["0x0", "0x0", "0x0"] },
                                { "pc": 8, "op": "PUSH1", "gas": 46468, "gasCost": 3, "depth": 1, "stack": ["0x0", "0x0", "0x0", "0x0"] },
                                { "pc": 10, "op": "PUSH20", "gas": 46465, "gasCost": 3, "depth": 1, "stack": ["0x0", "0x0", "0x0", "0x0", "0x0"] },
                                { "pc": 31, "op": "PUSH3", "gas": 46462, "gasCost": 3, "depth": 1, "stack": ["0x0", "0x0", "0x0", "0x0", "0x0", "0x28156f6fdeeffd5667d51bb8d7d5069a920e0837"] },
                                { "pc": 35, "op": "CALL", "gas": 46459, "gasCost": 45774, "depth": 1, "stack": ["0x0", "0x0", "0x0", "0x0", "0x0", "0x28156f6fdeeffd5667d51bb8d7d5069a920e0837", "0x186a0"] },
                                { "pc": 36, "op": "STOP", "gas": 43859, "gasCost": 0, "depth": 1, "stack": ["0x1"] }
                            ]
                        },
                        "txHash": "0xdb3d8694a97364e8628aeb18993520ea6bac0b65b02eed1abddaaed1ddd04e7b"
                    }
                ],
                "id": 67
            }
            """
        )
        { TestName = "Contract with blockMemoryTracer" };

        yield return new TestCaseData(
            transactions,
            new GethTraceOptions { Tracer = "{gasUsed: [], step: function(log) { this.gasUsed.push(log.getGas()); }, result: function() { return this.gasUsed; }, fault: function(){}}" },
            """
            {
                "jsonrpc": "2.0",
                "result": [
                    {
                        "result": [46536,46533,46530,46524,46521,46518,46515,46512,14280,14277,14274,12074,12300],
                        "txHash": "0xb5a78a1eda0ae98d4f62eec3e0b7f5bf81810cd57bc75006b611982667bcdbe7"
                    },
                    {
                        "result": [46480,46477,46474,46471,46468,46465,46462,46459,43859],
                        "txHash": "0xdb3d8694a97364e8628aeb18993520ea6bac0b65b02eed1abddaaed1ddd04e7b"
                    }
                ],
                "id": 67
            }
            """
        )
        { TestName = "Contract with javaScriptTracer" };

        yield return new TestCaseData(
            transactions,
            new GethTraceOptions { Tracer = Native4ByteTracer.FourByteTracer },
            """
            {
                "jsonrpc": "2.0",
                "result": [
                    {
                        "result": {
                            "0x7f600060-73": 1
                        },
                        "txHash": "0xb5a78a1eda0ae98d4f62eec3e0b7f5bf81810cd57bc75006b611982667bcdbe7"
                    },
                    {
                        "result": {
                            "0x60006000-33": 1
                        },
                        "txHash": "0xdb3d8694a97364e8628aeb18993520ea6bac0b65b02eed1abddaaed1ddd04e7b"
                    }
                ],
                "id": 67
            }
            """
        )
        { TestName = "Contract with " + Native4ByteTracer.FourByteTracer };

        yield return new TestCaseData(
            transactions,
            new GethTraceOptions { Tracer = NativeNoopTracer.NoopTracer },
            """
            {
                "jsonrpc": "2.0",
                "result": [
                    {
                        "result": {},
                        "txHash": "0xb5a78a1eda0ae98d4f62eec3e0b7f5bf81810cd57bc75006b611982667bcdbe7"
                    },
                    {
                        "result": {},
                        "txHash": "0xdb3d8694a97364e8628aeb18993520ea6bac0b65b02eed1abddaaed1ddd04e7b"
                    }
                ],
                "id": 67
            }
            """
        )
        { TestName = "Contract with " + NativeNoopTracer.NoopTracer };

        yield return new TestCaseData(
            transactions,
            new GethTraceOptions { Tracer = NativeCallTracer.CallTracer },
            """
            {
                "jsonrpc": "2.0",
                "result": [
                    {
                        "result": {
                            "type": "CREATE",
                            "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
                            "to": "0x0ffd3e46594919c04bcfd4e146203c8255670828",
                            "value": "0x1",
                            "gas": "0x186a0",
                            "gasUsed": "0x15694",
                            "input": "0x7f60006020550000000000000000000000000000000000000000000000000000006000527f0000000000000000000000000000000000000000000000000000000000000000600660006000f500",
                            "calls": [
                                {
                                    "type": "CREATE2",
                                    "from": "0x0ffd3e46594919c04bcfd4e146203c8255670828",
                                    "to": "0x28156f6fdeeffd5667d51bb8d7d5069a920e0837",
                                    "value": "0x0",
                                    "gas": "0x37c8",
                                    "gasUsed": "0x89e",
                                    "input": "0x600060205500"
                                }
                            ]
                        },
                        "txHash": "0xb5a78a1eda0ae98d4f62eec3e0b7f5bf81810cd57bc75006b611982667bcdbe7"
                    },
                    {
                        "result": {
                            "type": "CREATE",
                            "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
                            "to": "0x6b5887043de753ecfa6269f947129068263ffbe2",
                            "value": "0x1",
                            "gas": "0x186a0",
                            "gasUsed": "0xdb4d",
                            "input": "0x600060006000600060007328156f6fdeeffd5667d51bb8d7d5069a920e0837620186a0f100",
                            "calls": [
                                {
                                    "type": "CALL",
                                    "from": "0x6b5887043de753ecfa6269f947129068263ffbe2",
                                    "to": "0x28156f6fdeeffd5667d51bb8d7d5069a920e0837",
                                    "value": "0x0",
                                    "gas": "0xa8a6",
                                    "gasUsed": "0x0",
                                    "input": "0x"
                                }
                            ]
                        },
                        "txHash": "0xdb3d8694a97364e8628aeb18993520ea6bac0b65b02eed1abddaaed1ddd04e7b"
                    }
                ],
                "id": 67
            }
            """
        )
        { TestName = "Contract with " + NativeCallTracer.CallTracer };

        yield return new TestCaseData(
            transactions,
            new GethTraceOptions { Tracer = NativePrestateTracer.PrestateTracer },
            """
            {
                "jsonrpc": "2.0",
                "result": [
                    {
                        "result": {
                            "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
                                "balance": "0x3635c9adc5de9f09e5",
                                "nonce": 3,
                                "code": "0xabcd"
                            },
                            "0x0ffd3e46594919c04bcfd4e146203c8255670828": {
                                "balance": "0x0"
                            },
                            "0x475674cb523a0a2736b7f7534390288fce16982c": {
                                "balance": "0xf618"
                            },
                            "0x28156f6fdeeffd5667d51bb8d7d5069a920e0837": {
                                "balance": "0x0",
                                "storage": {
                                    "0x0000000000000000000000000000000000000000000000000000000000000020": "0x0000000000000000000000000000000000000000000000000000000000000000"
                                }
                            }
                        },
                        "txHash": "0xb5a78a1eda0ae98d4f62eec3e0b7f5bf81810cd57bc75006b611982667bcdbe7"
                    },
                    {
                        "result": {
                            "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
                                "balance": "0x3635c9adc5de9db350",
                                "nonce": 4,
                                "code": "0xabcd"
                            },
                            "0x6b5887043de753ecfa6269f947129068263ffbe2": {
                                "balance": "0x0"
                            },
                            "0x475674cb523a0a2736b7f7534390288fce16982c": {
                                "balance": "0x24cac"
                            },
                            "0x28156f6fdeeffd5667d51bb8d7d5069a920e0837": {
                                "balance": "0x0",
                                "nonce": 1
                            }
                        },
                        "txHash": "0xdb3d8694a97364e8628aeb18993520ea6bac0b65b02eed1abddaaed1ddd04e7b"
                    }
                ],
                "id": 67
            }
            """
        )
        { TestName = "Contract with " + NativePrestateTracer.PrestateTracer };
    }

    private static Transaction[] CreateTraceBlockTransactions(TestRpcBlockchain blockchain)
    {
        ulong nonce = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        byte[] contract = Prepare.EvmCode
            .PushData(0)
            .PushData(32)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        byte[] salt = new byte[32];
        byte[] deployment = Prepare.EvmCode
            .Create2(contract, salt, 0)
            .Op(Instruction.STOP)
            .Done;

        Address deployingContractAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, nonce);
        Address deploymentAddress = ContractAddress.From(deployingContractAddress, salt, contract);

        byte[] call = Prepare.EvmCode
            .Call(deploymentAddress, 100000)
            .Op(Instruction.STOP)
            .Done;

        return
        [
            Build.A.Transaction
                .WithNonce(nonce)
                .WithCode(deployment)
                .WithGasLimit(100000)
                .SignedAndResolved(TestItem.PrivateKeyA)
                .TestObject,

            Build.A.Transaction
                .WithNonce(nonce + 1)
                .WithCode(call)
                .WithGasLimit(100000)
                .SignedAndResolved(TestItem.PrivateKeyA)
                .TestObject,
        ];
    }

    [Test]
    public async Task GethLikeTxTraceStreamingResult_WriteToAsync_produces_same_json_as_serializer([Values(1, 100, 1000)] int traceCount)
    {
        List<GethLikeTxTrace> traces = new(traceCount);
        for (int i = 0; i < traceCount; i++)
        {
            GethLikeTxTrace trace = new();
            trace.TxHash = new Core.Crypto.Hash256(Keccak.Compute(i.ToString()).Bytes);
            trace.Entries.Add(new GethTxTraceEntry { ProgramCounter = i, Opcode = "STOP", Gas = 21000, GasCost = 0, Depth = 1 });
            traces.Add(trace);
        }

        using GethLikeTxTraceStreamingResult result = new(traces);

        string streamedJson = await StreamToStringAsync(result);

        string stjJson = JsonSerializer.Serialize(result, EthereumJsonSerializer.JsonOptions);

        Assert.That(JsonElement.DeepEquals(
            JsonDocument.Parse(streamedJson).RootElement,
            JsonDocument.Parse(stjJson).RootElement),
            $"Streamed JSON differs from serializer output for {traceCount} traces");
    }

    [Test]
    public async Task Streamed_call_trace_nested_to_max_call_depth_serializes([Values] bool blockResult)
    {
        using NativeCallTracerCallFrame root = new() { Type = Instruction.CALL, Error = "a<b" };
        NativeCallTracerCallFrame current = root;
        for (int depth = 1; depth < VirtualMachineStatics.MaxCallDepth; depth++)
        {
            NativeCallTracerCallFrame child = new() { Type = Instruction.CALL };
            current.Calls.Add(child);
            current = child;
        }

        GethLikeTxTrace trace = new() { TxHash = TestItem.KeccakA, CustomTracerResult = new GethLikeCustomTrace { Value = root } };
        using IDisposable result = blockResult
            ? new GethLikeTxTraceStreamingBlockResult(
                (writer, _, _) => JsonSerializer.Serialize(writer, trace, EthereumJsonSerializer.JsonOptions),
                new CancellationTokenSource(),
                LimboLogs.Instance.GetClassLogger<DebugRpcModuleTests>())
            : new GethLikeTxTraceStreamingResult([trace]);

        string streamedJson = await StreamToStringAsync((IStreamableResult)result);

        using JsonDocument document = JsonDocument.Parse(streamedJson, new JsonDocumentOptions { MaxDepth = EthereumJsonSerializer.DefaultMaxDepth });
        Assert.That(streamedJson, Does.Contain("\"error\":\"a<b\""), "string values must use the serializer's encoder");
    }

    private static async Task<string> StreamToStringAsync(IStreamableResult result)
    {
        // remove buffer limit hits from the equation by using an unbounded Pipe
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: 0));
        await result.WriteToAsync(pipe.Writer, CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        ReadResult readResult = await pipe.Reader.ReadAsync();
        string streamedJson = Encoding.UTF8.GetString(readResult.Buffer);
        pipe.Reader.AdvanceTo(readResult.Buffer.End);
        return streamedJson;
    }

    [Test]
    public async Task Debug_traceBlock_returns_error_for_genesis([Values("debug_traceBlockByNumber", "debug_traceBlockByHash")] string method)
    {
        using Context context = await Context.Create();

        object? arg = method switch
        {
            "debug_traceBlockByNumber" => context.Blockchain.BlockTree.Genesis!.Number,
            _ => context.Blockchain.BlockTree.Genesis!.Hash
        };

        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, arg, new GethTraceOptions());

        using JsonDocument doc = JsonDocument.Parse(response);
        JsonElement root = doc.RootElement;

        Assert.That(root.TryGetProperty("error", out JsonElement error), Is.True, "Missing 'error' field");
        Assert.That(error.GetProperty("message").GetString(), Is.EqualTo("genesis is not traceable"));
        Assert.That(error.GetProperty("code").GetInt32(), Is.EqualTo(-32000));
    }

    [Test]
    public async Task Debug_traceBlock_json_rpc_request_returns_valid_json([Values("debug_traceBlock", "debug_traceBlockByNumber", "debug_traceBlockByHash")] string method)
    {
        using Context context = await Context.Create();
        await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));

        object? arg = method switch
        {
            "debug_traceBlock" => Rlp.Encode(context.Blockchain.BlockTree.Head).ToString(),
            "debug_traceBlockByNumber" => context.Blockchain.BlockTree.Head!.Number,
            _ => context.Blockchain.BlockTree.Head!.Hash
        };

        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, arg, new GethTraceOptions());

        TestContext.Out.WriteLine(response);

        using JsonDocument doc = JsonDocument.Parse(response);
        JsonElement root = doc.RootElement;

        Assert.That(root.TryGetProperty("result", out JsonElement resultArray), Is.True, "Missing 'result' field");
        Assert.That(resultArray.ValueKind, Is.EqualTo(JsonValueKind.Array), "'result' must be an array");
        Assert.That(resultArray.GetArrayLength(), Is.GreaterThan(0), "Result array must not be empty");

        foreach (JsonElement entry in resultArray.EnumerateArray())
        {
            Assert.That(entry.TryGetProperty("result", out _), Is.True, "Each entry must have 'result'");
            Assert.That(entry.TryGetProperty("txHash", out _), Is.True, "Each entry must have 'txHash'");
        }
    }

}
