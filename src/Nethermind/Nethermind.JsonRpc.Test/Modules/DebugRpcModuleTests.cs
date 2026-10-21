// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Messages;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

[Parallelizable(ParallelScope.Self)]
public partial class DebugRpcModuleTests
{
    private static IEnumerable<TestCaseData> ValidTransactionIndices()
    {
        (string Json, ulong? Value)[] cases =
        [
            ("null", null),
            ("\"0x0\"", 0),
            ("\"0XABCDEF\"", 0xabcdef),
            ("\"0xffffffffffffffff\"", ulong.MaxValue),
            ("\"\\u0030\\u0078\\u0031\"", 1),
            ("\"\\u0030\\u0078" + new string('f', 16).Replace("f", "\\u0066") + "\"", ulong.MaxValue)
        ];
        foreach ((string json, ulong? value) in cases)
            foreach (bool segmented in new[] { false, true })
                yield return new TestCaseData(json, value, segmented);
    }

    [TestCaseSource(nameof(ValidTransactionIndices))]
    public void Trace_options_read_transaction_index_quantities(string json, ulong? expected, bool segmented) =>
        Assert.That(ReadTransactionIndex(json, segmented), Is.EqualTo(expected));

    private static IEnumerable<TestCaseData> InvalidTransactionIndices()
    {
        string[] cases =
        [
            "0", "true", "[]", "{}", "\"\"", "\"0x\"", "\"0x00\"", "\"0xg\"", "\"0x1 \"",
            "\"0x10000000000000000\"", "\"\\u0030\\u0078\\u0030\\u0030\"",
            "\"0x" + new string('f', 107) + "\""
        ];
        foreach (string json in cases)
            foreach (bool segmented in new[] { false, true })
                yield return new TestCaseData(json, segmented);
    }

    [TestCaseSource(nameof(InvalidTransactionIndices))]
    public void Trace_options_reject_invalid_transaction_index_quantities(string json, bool segmented) =>
        Assert.Throws<JsonException>(() => ReadTransactionIndex(json, segmented));

    [TestCase("\"0x1\"")]
    [TestCase("\"\\u0030\\u0078\\u0031\"")]
    public void Transaction_index_reader_does_not_allocate_strings(string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        GethTraceOptions.TransactionIndexConverter converter = new();
        JsonSerializerOptions options = new();
        ulong Read()
        {
            Utf8JsonReader reader = new(bytes);
            reader.Read();
            return converter.Read(ref reader, typeof(ulong), options);
        }
        Assert.That(Read(), Is.EqualTo(1UL));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) Read();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(allocated, Is.Zero);
    }

    private static ulong? ReadTransactionIndex(string json, bool segmented)
    {
        const string prefix = "{\"txIndex\":";
        byte[] bytes = Encoding.UTF8.GetBytes(prefix + json + "}");
        if (!segmented) return JsonSerializer.Deserialize<GethTraceOptions>(bytes, EthereumJsonSerializer.JsonOptions)!.TxIndex;
        int split = prefix.Length + Encoding.UTF8.GetByteCount(json) / 2;
        TransactionIndexJsonSegment first = new(bytes.AsMemory(0, split));
        TransactionIndexJsonSegment last = first.Append(bytes.AsMemory(split));
        Utf8JsonReader reader = new(new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length));
        Utf8JsonReader tokenReader = reader;
        tokenReader.Read();
        tokenReader.Read();
        tokenReader.Read();
        if (tokenReader.TokenType == JsonTokenType.String && json.Length > 2)
            Assert.That(tokenReader.HasValueSequence, Is.True, "the quantity must cross the buffer boundary");
        return JsonSerializer.Deserialize<GethTraceOptions>(ref reader, EthereumJsonSerializer.JsonOptions)!.TxIndex;
    }

    private sealed class TransactionIndexJsonSegment : ReadOnlySequenceSegment<byte>
    {
        public TransactionIndexJsonSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public TransactionIndexJsonSegment Append(ReadOnlyMemory<byte> memory)
        {
            TransactionIndexJsonSegment next = new(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    [Test]
    public async Task Debug_traceCall_txIndex_uses_prefix_before_overrides(
        [Values(-1, 0, 1, 2)] int index,
        [Values("buffered", "streamed", "callTracer")] string mode,
        [Values] bool overridePrefixSender,
        [Values] bool blockAccessLists)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = mode == "streamed" })
            .Build(builder => builder.AddSingleton<ISpecProvider>(new TestSpecProvider(blockAccessLists ? Amsterdam.Instance : Prague.Instance) { AllowTestChainOverride = false }));
        BlockHeader parent = chain.BlockTree.Head!.Header;
        UInt256 initial = chain.WorldStateManager.GlobalStateReader.GetBalance(parent, TestItem.AddressC);
        UInt256 senderBalance = chain.WorldStateManager.GlobalStateReader.GetBalance(parent, TestItem.AddressB);
        Block block = await AddTraceCallPrefixTransfers(chain, 3);
        Transaction[] transactions = block.Transactions;
        string canonicalHeader = Nethermind.Serialization.Rlp.Rlp.Encode(block.Header).ToString();
        int prefixLength = index < 0 ? 3 : index;
        for (int i = 0; i < prefixLength; i++)
            senderBalance -= transactions[i].Value + transactions[i].GasPrice * transactions[i].BlockGasUsed;
        if (overridePrefixSender) senderBalance = UInt256.Zero;
        byte[] code = Prepare.EvmCode.PushData(TestItem.AddressC).Op(Instruction.BALANCE).PushData(0).Op(Instruction.MSTORE)
            .PushData(TestItem.AddressB).Op(Instruction.BALANCE).PushData(32).Op(Instruction.MSTORE)
            .Op(Instruction.NUMBER).PushData(64).Op(Instruction.MSTORE).Return(96, 0).Done;
        Dictionary<string, object> overrides = new()
        {
            [TestItem.AddressD.ToString()] = new { code = code.ToHexString(true) }
        };
        if (overridePrefixSender) overrides[TestItem.AddressB.ToString()] = new { balance = "0x0" };
        object call = new { from = TestItem.AddressA.ToString(), to = TestItem.AddressD.ToString(), gas = "0x186a0" };
        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceCall", call, "latest",
            new
            {
                txIndex = index < 0 ? null : $"0x{index:x}",
                tracer = mode == "callTracer" ? "callTracer" : null,
                stateOverrides = overrides,
                blockOverrides = new { number = "0x4d2" }
            });
        JToken json = JToken.Parse(response);
        Assert.That(json["error"], Is.Null, response);
        string output = (string)json["result"]![mode == "callTracer" ? "output" : "returnValue"]!;
        string expected = (initial + (UInt256)prefixLength).ToBigEndian().ToHexString()
            + senderBalance.ToBigEndian().ToHexString() + ((UInt256)1234).ToBigEndian().ToHexString();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((output.StartsWith("0x") ? output[2..] : output), Is.EqualTo(expected), response);
            Assert.That(Nethermind.Serialization.Rlp.Rlp.Encode(block.Header).ToString(), Is.EqualTo(canonicalHeader));
            Assert.That(chain.WorldStateManager.GlobalStateReader.GetBalance(block.Header, TestItem.AddressC), Is.EqualTo(initial + 3));
        }
    }

    [Test]
    public async Task Debug_traceCall_txIndex_keeps_block_overrides_out_of_prefix([Values] bool blockAccessLists)
    {
        OverridableReleaseSpec spec = new(blockAccessLists ? Amsterdam.Instance : Prague.Instance) { Eip1559TransitionBlock = 1 };
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(builder => builder.AddSingleton<ISpecProvider>(new TestSpecProvider(spec) { AllowTestChainOverride = false }));
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressB);
        byte[] initCode = Prepare.EvmCode.Op(Instruction.NUMBER).PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.BASEFEE).PushData(1).Op(Instruction.SSTORE).STOP().Done;
        Transaction create = Build.A.Transaction.WithCode(initCode).WithNonce(nonce).WithGasPrice(1_000_000_000)
            .WithGasLimit(1_000_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Transaction transfer = Build.A.Transaction.WithTo(TestItem.AddressC).WithNonce(nonce + 1).WithGasPrice(1_000_000_000)
            .WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block block = await chain.AddBlock(create, transfer);
        Assert.That(block.Transactions, Has.Length.EqualTo(2));
        Assert.That(block.Header.BaseFeePerGas, Is.Not.EqualTo(UInt256.Zero));
        Address contract = ContractAddress.From(TestItem.AddressB, nonce);
        TxReceipt receipt = chain.ReceiptStorage.Get(block)[0];
        Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(receipt.ContractAddress, Is.EqualTo(contract));
        byte[] probe = Prepare.EvmCode.PushData(0).Op(Instruction.SLOAD).PushData(0).Op(Instruction.MSTORE)
            .PushData(1).Op(Instruction.SLOAD).PushData(32).Op(Instruction.MSTORE)
            .Op(Instruction.NUMBER).PushData(64).Op(Instruction.MSTORE)
            .Op(Instruction.BASEFEE).PushData(96).Op(Instruction.MSTORE).Return(128, 0).Done;
        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceCall",
            new { to = contract.ToString(), gas = "0x186a0" }, block.Hash!.ToString(), new
            {
                txIndex = "0x1",
                blockOverrides = new { number = "0x4d2" },
                stateOverrides = new Dictionary<string, object> { [contract.ToString()] = new { code = probe.ToHexString(true) } }
            });
        JToken json = JToken.Parse(response);
        string expected = ((UInt256)block.Number).ToBigEndian().ToHexString() + block.Header.BaseFeePerGas.ToBigEndian().ToHexString()
            + ((UInt256)1234).ToBigEndian().ToHexString() + UInt256.Zero.ToBigEndian().ToHexString();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(json["error"], Is.Null, response);
            Assert.That((string?)json["result"]?["returnValue"], Is.EqualTo("0x" + expected), response);
        }
    }

    [Test]
    public async Task Debug_traceCall_txIndex_javascript_context_uses_call_overrides(
        [Values(-1, 0, 1)] int index, [Values] bool blockAccessLists)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(builder => builder.AddSingleton<ISpecProvider>(new TestSpecProvider(blockAccessLists ? Amsterdam.Instance : Prague.Instance) { AllowTestChainOverride = false }));
        await AddTraceCallPrefixTransfers(chain, 2);
        const string tracer = "{step:function(){},fault:function(){},result:function(ctx){return {block:ctx.block,gasPrice:ctx.gasPrice.toString(10)};}}";
        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceCall",
            new { from = TestItem.AddressA.ToString(), to = TestItem.AddressD.ToString(), gas = "0x186a0" }, "latest",
            new
            {
                txIndex = index < 0 ? null : $"0x{index:x}",
                tracer,
                blockOverrides = new { number = "0x4d2" },
                stateOverrides = new Dictionary<string, object> { [TestItem.AddressD.ToString()] = new { code = "0x00" } }
            });
        JToken json = JToken.Parse(response);
        Assert.That(json["error"], Is.Null, response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int?)json["result"]!["block"], Is.EqualTo(1234), response);
            Assert.That((string?)json["result"]!["gasPrice"], Is.EqualTo("0"), response);
        }
    }

    [Test]
    public async Task Debug_traceCall_txIndex_creation_uses_overridden_sender_nonce(
        [Values(-1, 0, 1)] int index, [Values] bool blockAccessLists)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(builder => builder.AddSingleton<ISpecProvider>(new TestSpecProvider(blockAccessLists ? Amsterdam.Instance : Prague.Instance) { AllowTestChainOverride = false }));
        await AddTraceCallPrefixTransfers(chain, 2);
        const ulong overriddenNonce = 37;
        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceCall",
            new { from = TestItem.AddressA.ToString(), gas = "0x989680", data = "0x00" }, "latest",
            new
            {
                txIndex = index < 0 ? null : $"0x{index:x}",
                tracer = "callTracer",
                stateOverrides = new Dictionary<string, object> { [TestItem.AddressA.ToString()] = new { nonce = $"0x{overriddenNonce:x}" } }
            });
        JToken json = JToken.Parse(response);
        Assert.That(json["error"], Is.Null, response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((string?)json["result"]!["type"], Is.EqualTo("CREATE"), response);
            Assert.That((string?)json["result"]!["to"], Is.EqualTo(ContractAddress.From(TestItem.AddressA, overriddenNonce).ToString()), response);
        }
    }

    private static async Task<Block> AddTraceCallPrefixTransfers(TestRpcBlockchain chain, int count)
    {
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressB);
        Transaction[] transactions = new Transaction[count];
        for (int i = 0; i < transactions.Length; i++)
            transactions[i] = Build.A.Transaction.WithTo(TestItem.AddressC).WithNonce(nonce + (ulong)i)
                .WithValue(1).WithGasPrice(1_000_000_000).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block block = await chain.AddBlock(transactions);
        Assert.That(block.Transactions, Has.Length.EqualTo(count));
        return block;
    }

    [TestCase("0x3", -32000)]
    [TestCase("0xffffffffffffffff", -32000)]
    [TestCase("0", -32602)]
    [TestCase("-1", -32602)]
    [TestCase("0x00", -32602)]
    [TestCase("0x", -32602)]
    [TestCase("0xg", -32602)]
    [TestCase("0x10000000000000000", -32602)]
    [TestCase("NUMBER", -32602)]
    public async Task Debug_traceCall_txIndex_rejects_invalid_input(string value, int errorCode)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        object index = value == "NUMBER" ? 0 : value;
        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceCall",
            new { to = TestItem.AddressC.ToString(), gas = "0x186a0" }, "latest", new { txIndex = index });
        Assert.That((int?)JToken.Parse(response)["error"]?["code"], Is.EqualTo(errorCode), response);
    }

    [Test]
    public async Task Debug_traceCall_txIndex_rejects_end_of_nonempty_block([Values] bool streaming, [Values] bool conflictingStorage)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = streaming }).Build();
        Block block = await AddTraceCallPrefixTransfers(chain, 3);
        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceCall",
            new { to = TestItem.AddressD.ToString(), gas = "0x186a0" }, block.Hash!.ToString(),
            new
            {
                txIndex = $"0x{block.Transactions.Length:x}",
                stateOverrides = conflictingStorage
                    ? new Dictionary<string, object> { [TestItem.AddressD.ToString()] = new { state = new { }, stateDiff = new { } } }
                    : null
            });
        JToken json = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int?)json["error"]?["code"], Is.EqualTo(-32000), response);
            Assert.That((string?)json["error"]?["message"], Is.EqualTo($"transaction index {block.Transactions.Length} out of range for block {block.Hash}"), response);
            Assert.That(json["result"], Is.Null, response);
        }
    }

    [Test]
    public async Task Debug_traceCall_txIndex_checks_parent_state_before_bounds()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        Block parent = chain.BlockTree.Head!;
        Block empty = await chain.AddBlock();
        parent.Header.StateRoot = TestItem.KeccakA;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.Bridge.HasStateForBlock(parent.Header), Is.False);
            Assert.That(chain.Bridge.HasStateForBlock(empty.Header), Is.True);
        }

        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceCall",
            new { to = TestItem.AddressD.ToString(), gas = "0x186a0" }, empty.Hash!.ToString(), new { txIndex = "0x1" });
        JToken json = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int?)json["error"]?["code"], Is.EqualTo(ErrorCodes.ResourceUnavailable), response);
            Assert.That((string?)json["error"]?["message"],
                Is.EqualTo($"No state available for block {parent.Header.ToString(BlockHeader.Format.FullHashAndNumber)}"), response);
        }
    }

    [TestCase(true, "0x0", -32000, false)]
    [TestCase(true, "0x0", -32000, true)]
    [TestCase(false, "0x0", null, false)]
    [TestCase(false, "0x0", null, true)]
    [TestCase(false, "0x1", -32000, false)]
    [TestCase(false, "0x1", -32000, true)]
    public async Task Debug_traceCall_txIndex_empty_and_genesis(bool genesis, string index, int? errorCode, bool stream)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = stream }).Build();
        Block empty = await chain.AddBlock();
        Assert.That(empty.Transactions, Is.Empty);
        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceCall",
            new { to = TestItem.AddressD.ToString(), gas = "0x186a0" }, genesis ? "0x0" : empty.Hash!.ToString(), new { txIndex = index });
        JToken json = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int?)json["error"]?["code"], Is.EqualTo(errorCode), response);
            if (errorCode is null) Assert.That((bool?)json["result"]?["failed"], Is.False, response);
            if (genesis) Assert.That((string?)json["error"]?["message"], Is.EqualTo("no transaction in genesis"), response);
        }
    }

    public static IEnumerable<TestCaseData> TransactionTracingPrefixCases()
    {
        foreach (string method in new[] { "debug_traceTransaction", "trace_transaction", "trace_replayTransaction" })
        {
            for (int targetIndex = 0; targetIndex <= 2; targetIndex++)
            {
                yield return new TestCaseData(method, targetIndex, false, false);
                yield return new TestCaseData(method, targetIndex, true, false);
            }

            yield return new TestCaseData(method, 0, false, true);
            yield return new TestCaseData(method, 0, true, true);
        }
    }

    [TestCaseSource(nameof(TransactionTracingPrefixCases))]
    public async Task TransactionTracing_WhenTargetSelected_ExecutesOnlyPrefix(
        string method, int targetIndex, bool stream, bool unsupportedValidationModule)
    {
        List<Hash256?> executed = [];
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { Timeout = -1, EnableTracingStreamMode = stream })
            .Build(builder =>
            {
                builder.AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance) { AllowTestChainOverride = false })
                    .AddDecorator<ITransactionProcessorAdapter>((_, inner) => new PrefixCountingAdapter(inner, executed));
                if (unsupportedValidationModule) builder.AddSingleton<IBlockValidationModule, UnsupportedTraceValidationModule>();
            });
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressB);
        Transaction[] transactions = new Transaction[3];
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = Build.A.Transaction.WithTo(TestItem.AddressC).WithNonce(nonce + (ulong)i)
                .WithValue((UInt256)(i + 1)).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        }
        Block block = await chain.AddBlock(transactions);
        Assert.That(block.Transactions.Length, Is.EqualTo(3), "precondition: all three test transactions must be mined");
        executed.Clear();
        string hash = block.Transactions[targetIndex].Hash!.ToString();
        string response = method switch
        {
            "debug_traceTransaction" => await RpcTest.TestSerializedRequest(chain.DebugRpcModule, method, hash, new { tracer = "callTracer" }),
            "trace_replayTransaction" => await RpcTest.TestSerializedRequest(chain.TraceRpcModule, method, hash, new[] { "trace", "stateDiff" }),
            _ => await RpcTest.TestSerializedRequest(chain.TraceRpcModule, method, hash)
        };

        JToken json = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(json["error"], Is.Null, "the prefix must produce a successful RPC response");
            Assert.That(json["result"], Is.Not.Null, "the selected transaction must have a trace result");
            Assert.That(executed.Count, Is.EqualTo(unsupportedValidationModule ? 3 : targetIndex + 1),
                "an additional validation module without explicit prefix support must retain full replay");
        }
        for (int i = 0; i < executed.Count; i++)
            Assert.That(executed[i], Is.EqualTo(block.Transactions[i].Hash), "prefix order and original transaction identities must be preserved");
    }

    private sealed class UnsupportedTraceValidationModule : Module, IBlockValidationModule;

    private class Context : IDisposable
    {
        public IDebugRpcModule DebugRpcModule { get; }
        public TestRpcBlockchain Blockchain { get; }

        private Context(TestRpcBlockchain blockchain, IDebugRpcModule debugRpcModule)
        {
            DebugRpcModule = debugRpcModule;
            Blockchain = blockchain;
        }

        public static async Task<Context> Create(ISpecProvider? specProvider = null, bool isAura = false)
        {
            TestRpcBlockchain blockchain = await TestRpcBlockchain.ForTest(isAura ? SealEngineType.AuRa : SealEngineType.NethDev).Build(specProvider);

            IDebugRpcModule debugRpcModule = blockchain.DebugRpcModule;

            return new(blockchain, debugRpcModule);
        }

        public void Dispose() => Blockchain.Dispose();
    }

    [TestCaseSource(nameof(TraceCallGethCompatFailureCases))]
    public async Task Debug_traceCall_failure_returns_geth_compatible_error(object txArgs, string expectedErrorPrefix, int expectedErrorCode)
    {
        using Context ctx = await Context.Create();

        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", txArgs);

        JToken result = JToken.Parse(response)["result"]!;
        Assert.That(((bool)result["failed"]!), Is.True, "pre-flight failures surface as failed:true under streaming");
        Assert.That(((string)result["returnValue"]!), Is.EqualTo("0x"));
        Assert.That(((string)result["error"]!), Does.StartWith(expectedErrorPrefix), "Geth-compatible wording must be preserved verbatim");
        Assert.That(((int)result["errorCode"]!), Is.EqualTo(expectedErrorCode), "errorCode mirrors Geth's per-scenario JSON-RPC code");
    }

    private static IEnumerable<TestCaseData> TraceCallGethCompatFailureCases()
    {
        Address freshA = Build.An.Address.TestObject;
        yield return new TestCaseData(
            (object)new { from = $"{freshA}", to = $"{TestItem.AddressC}", value = 50.Ether.ToString("X") },
            $"tracing failed: {TxErrorMessages.InsufficientFundsForGas}: address ",
            ErrorCodes.InvalidInput)
        { TestName = "InsufficientFundsForTransfer" };

        Address freshB = Build.An.Address.TestObject;
        yield return new TestCaseData(
            (object)new { from = $"{freshB}", to = $"{TestItem.AddressC}", gas = "0x64" },
            "tracing failed: intrinsic gas too low: have 100, want ",
            ErrorCodes.InvalidInput)
        { TestName = "IntrinsicGasTooLow" };

        Address freshC = Build.An.Address.TestObject;
        yield return new TestCaseData(
            (object)new { from = $"{freshC}", to = $"{TestItem.AddressC}", gasPrice = "0x1" },
            $"tracing failed: {TxErrorMessages.InsufficientFundsForGas}: address ",
            ErrorCodes.InvalidInput)
        { TestName = "InsufficientFundsForGasPriceValue" };
    }

    public static IEnumerable<TestCaseData> KeccakPreimageCases()
    {
        (string Name, string Code, string Expected, long Gas)[] cases =
        [
            ("none", "00", """{}""", 100_000),
            ("empty", "600060002000", """{"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470":"0x"}""", 100_000),
            ("padded", "600160002000", """{"0xbc36789e7a1e281436464229828f817d6612f7b477d66591ff96a9e064bcc98a":"0x00"}""", 100_000),
            ("byte", "602a600053600160002000", """{"0x04994f67dc55b09e814ab7ffc8df3686b4afb2bb53e60eae97ef043fe03fb829":"0x2a"}""", 100_000),
            ("overlap", "602a601f536002601f2000", """{"0x71378d9bd65a614e4926f9fa621eae99b3f2a1cf19e8c22e41594dc15c16dd33":"0x2a00"}""", 100_000),
            ("offset", "600160202000", """{"0xbc36789e7a1e281436464229828f817d6612f7b477d66591ff96a9e064bcc98a":"0x00"}""", 100_000),
            ("duplicate", "6000600020600060002000", """{"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470":"0x"}""", 100_000),
            ("revert", "602a600053600160002060006000fd", """{"0x04994f67dc55b09e814ab7ffc8df3686b4afb2bb53e60eae97ef043fe03fb829":"0x2a"}""", 100_000),
            ("empty_stack", "20", """{}""", 100_000),
            ("short_stack", "600020", """{}""", 100_000),
            ("padding_limit", "6001621000012000", """{}""", 100_000),
            ("negative_offset", "600167ffffffffffffffff2000", """{}""", 100_000),
            ("signed_end_overflow", "6740000000000000006740000000000000002000", """{"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470":"0x"}""", 100_000),
            ("large_size", "63ffffffff60002000", """{}""", 100_000),
            ("truncated_offset", "60007f01000000000000000000000000000000000000000000000000000000000000002000", """{"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470":"0x"}""", 100_000),
            ("distinct", "6000600020600160002000", """{"0xbc36789e7a1e281436464229828f817d6612f7b477d66591ff96a9e064bcc98a":"0x00","0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470":"0x"}""", 100_000),
            ("out_of_gas", "6001620fffff2000", """{"0xbc36789e7a1e281436464229828f817d6612f7b477d66591ff96a9e064bcc98a":"0x00"}""", 100_000),
            ("nested_revert", "6000600060006000600073000000000000000000000000000000000000901261c350f150600160002000", """{"0x04994f67dc55b09e814ab7ffc8df3686b4afb2bb53e60eae97ef043fe03fb829":"0x2a","0xbc36789e7a1e281436464229828f817d6612f7b477d66591ff96a9e064bcc98a":"0x00"}""", 100_000),
            ("unwritten_memory", "60016210080060006000600073000000000000000000000000000000000000901361c350f1506001621008002000", """{"0xbc36789e7a1e281436464229828f817d6612f7b477d66591ff96a9e064bcc98a":"0x00"}""", 3_000_000),
        ];
        foreach ((string name, string code, string expected, long gas) in cases)
        {
            foreach (bool disableStack in new[] { false, true })
            {
                yield return new TestCaseData(code, expected, disableStack, gas)
                    .SetName($"Debug_traceCall_keccak_preimages_{name}_disableStack_{disableStack}");
            }
        }
    }

    [TestCaseSource(nameof(KeccakPreimageCases))]
    public async Task Debug_traceCall_keccak_preimages(string code, string expected, bool disableStack, long gas)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Osaka.Instance));
        Dictionary<string, object> stateOverrides = new()
        {
            [TestItem.AddressC.ToString()] = new { code = "0x" + code },
            ["0x0000000000000000000000000000000000009012"] = new { code = "0x602a600053600160002060006000fd" }
        };
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { to = TestItem.AddressC.ToString(), gas = $"0x{gas:x}" }, "latest",
            new { tracer = "keccak256PreimageTracer", disableStack, enableMemory = false, stateOverrides });

        JToken result = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result["error"], Is.Null, response);
            Assert.That(JToken.DeepEquals(result["result"], JToken.Parse(expected)), Is.True, response);
        }
    }

    public static IEnumerable<TestCaseData> OpcodeLoggerLimitCases()
    {
        // Streamed counts pin Geth boundaries; buffered counts pin the conservative estimate.
        (string Code, bool DisableStack, bool EnableMemory, long Limit, int Count, int EstimatedCount)[] cases =
        [
            ("6000600060006000600073000000000000000000000000000000000000901261c350f15060206000f3", false, false, 770, 9, 3),
            ("6000600060006000600073000000000000000000000000000000000000901261c350f15060206000f3", false, false, 771, 10, 3),
            ("6000600060006000600073000000000000000000000000000000000000901261c350f15060206000f3", false, false, 1138, 14, 4),
            ("6000600060006000600073000000000000000000000000000000000000901261c350f15060206000f3", false, false, 1139, 15, 4),
            ("6000600060006000600073000000000000000000000000000000000000901261c350f15060206000f3", false, false, 1290, 15, 4),
            ("6000600060006000600073000000000000000000000000000000000000901261c350f15060206000f3", false, false, 1291, 16, 4),
            ("6000600060006000600073000000000000000000000000000000000000901261c350f15060206000f3", false, false, 1439, 16, 4),
            ("6000600060006000600073000000000000000000000000000000000000901261c350f15060206000f3", false, false, 1440, 17, 4),
            ("602a600055600060005500", false, false, 507, 5, 2),
            ("602a600055600060005500", false, false, 508, 6, 2),
            ("602a600055600060005500", false, false, 752, 6, 3),
            ("602a600055600060005500", false, false, 753, 7, 3),
            ("602a61ffff5360006000f3", false, true, 224, 3, 1),
            ("602a61ffff5360006000f3", false, true, 225, 4, 1),
            ("602a61ffff5360006000f3", false, true, 141613, 4, 4),
            ("602a61ffff5360006000f3", false, true, 141614, 5, 4),
            ("602a60005260206000f3", false, false, -1, 0, 0),
            ("602a60005260206000f3", false, false, 0, 6, 6),
            ("602a60005260206000f3", false, false, 1, 1, 1),
            ("602a60005260206000f3", false, false, 65, 1, 1),
            ("602a60005260206000f3", false, false, 66, 2, 1),
            ("602a60005260206000f3", false, false, 137, 2, 1),
            ("602a60005260206000f3", false, false, 138, 3, 1),
            ("602a60005260206000f3", false, false, 2147483648, 6, 6),
            ("602a60005260206000f3", true, false, 54, 1, 1),
            ("602a60005260206000f3", true, false, 55, 2, 1),
            ("602a60005260206000f3", true, false, 109, 2, 1),
            ("602a60005260206000f3", true, false, 110, 3, 1),
            ("602a60005260206000f3", false, true, 362, 4, 2),
            ("602a60005260206000f3", false, true, 363, 5, 2),
            ("602a60005560005460005260206000f3", false, false, 370, 3, 2),
            ("602a60005560005460005260206000f3", false, false, 371, 4, 2),
            ("602a60005560005460005260206000f3", false, false, 659, 5, 3),
            ("602a60005560005460005260206000f3", false, false, 660, 6, 3),
            ("602a60005260206000fd", false, false, -1, 0, 0),
            ("602a60005260206000fd", false, false, 1, 1, 1),
            ("602a60005260206000fd", false, false, 138, 3, 1),
        ];
        foreach ((string code, bool disableStack, bool enableMemory, long limit, int count, int estimatedCount) in cases)
        {
            foreach (bool streamMode in new[] { false, true })
                yield return new TestCaseData(code, disableStack, enableMemory, limit, streamMode ? count : estimatedCount, streamMode);
        }
    }

    [TestCaseSource(nameof(OpcodeLoggerLimitCases))]
    public async Task Debug_traceCall_opcode_logger_limit(
        string code, bool disableStack, bool enableMemory, long limit, int expectedCount, bool streamMode)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Osaka.Instance));
        Dictionary<string, object> stateOverrides = new()
        {
            [TestItem.AddressC.ToString()] = new { code = "0x" + code, state = new Dictionary<string, string>() },
            ["0x0000000000000000000000000000000000009012"] = new { code = "0x602a60005260206000f3" }
        };
        object call = new { to = TestItem.AddressC.ToString(), gas = "0x186a0" };
        string unlimitedResponse = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            call, "latest", new { streamMode, disableStack, enableMemory, enableReturnData = true, stateOverrides });
        string limitedResponse = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            call, "latest", new { streamMode, disableStack, enableMemory, enableReturnData = true, limit = JsonSerializer.SerializeToElement(limit), stateOverrides });

        JToken expected = JToken.Parse(unlimitedResponse)["result"]!;
        JArray entries = (JArray)expected["structLogs"]!;
        while (entries.Count > expectedCount)
            entries.RemoveAt(entries.Count - 1);

        JToken actual = JToken.Parse(limitedResponse);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual["error"], Is.Null, limitedResponse);
            Assert.That(JToken.DeepEquals(actual["result"], expected), Is.True, limitedResponse);
        }
    }

    // The first three opcode logs are small and each later one carries the 64 KiB of memory MSTORE8 grows,
    // so this cap ends the buffered trace at the fourth without depending on the exact size estimate.
    private static readonly long BufferedTraceCap = 64.KiB;

    [TestCase(false, 0L, 4)]
    [TestCase(false, long.MaxValue, 4)]
    [TestCase(false, 224L, 1)]
    [TestCase(false, -1L, 0)]
    [TestCase(true, 0L, 6)]
    public async Task Debug_traceCall_buffered_opcode_logs_are_capped_by_config(bool streamMode, long limit, int expectedCount)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Osaka.Instance));
        IJsonRpcConfig config = ctx.Blockchain.Container.Resolve<IJsonRpcConfig>();
        Dictionary<string, object> stateOverrides = new()
        {
            [TestItem.AddressC.ToString()] = new { code = "0x602a61ffff5360006000f3" }
        };
        object call = new { to = TestItem.AddressC.ToString(), gas = "0x186a0" };
        config.MaxBufferedTraceLogSize = 0;
        string unlimitedResponse = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            call, "latest", new { streamMode, enableMemory = true, stateOverrides });
        config.MaxBufferedTraceLogSize = BufferedTraceCap;
        string cappedResponse = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            call, "latest", new { streamMode, enableMemory = true, limit = JsonSerializer.SerializeToElement(limit), stateOverrides });

        JToken expected = JToken.Parse(unlimitedResponse)["result"]!;
        JArray entries = (JArray)expected["structLogs"]!;
        Assert.That(entries, Has.Count.EqualTo(6), unlimitedResponse);
        while (entries.Count > expectedCount)
            entries.RemoveAt(entries.Count - 1);

        JToken actual = JToken.Parse(cappedResponse);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual["error"], Is.Null, cappedResponse);
            Assert.That(JToken.DeepEquals(actual["result"], expected), Is.True, cappedResponse);
        }
    }

    [TestCase("null", null)]
    [TestCase("1", 1)]
    [TestCase("-9223372036854775808", 0)]
    [TestCase("9223372036854775807", null)]
    [TestCase("\"1\"", -32602)]
    [TestCase("\"0x1\"", -32602)]
    [TestCase("1.0", -32602)]
    [TestCase("true", -32602)]
    [TestCase("9223372036854775808", -32602)]
    public async Task Debug_traceCall_opcode_logger_limit_input(string limitJson, int? expected)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Osaka.Instance));
        Dictionary<string, object> stateOverrides = new()
        {
            [TestItem.AddressC.ToString()] = new { code = "0x602a60005260206000f3" }
        };
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { to = TestItem.AddressC.ToString(), gas = "0x186a0" }, "latest",
            new { limit = JsonSerializer.Deserialize<JsonElement>(limitJson), stateOverrides });
        JToken result = JToken.Parse(response);
        if (expected == -32602)
            Assert.That((int?)result["error"]?["code"], Is.EqualTo(ErrorCodes.InvalidParams), response);
        else
            Assert.That((JArray?)result["result"]?["structLogs"], Has.Count.EqualTo(expected ?? 6), response);
    }

    [TestCase("6000600060006000600073000000000000000000000000000000000000beef6000f1", 23000UL, "out of gas: out of gas")]
    [TestCase("600060006000600060017300000000000000000000000000000000000056786000f1", 29548UL, "out of gas: out of gas")]
    [TestCase("6001600055", 23306UL, "out of gas: not enough gas for reentrancy sentry")]
    [TestCase("6001600101", 21006UL, "out of gas")]
    public async Task Debug_traceCall_preserves_out_of_gas_reason(string bytecode, ulong gasLimit, string expectedError)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Osaka.Instance));
        string sender = TestItem.AddressA.ToString();
        string contract = TestItem.AddressC.ToString();
        const string balance = "0x100000000000000000000";
        Dictionary<string, object> stateOverrides = new()
        {
            [sender] = new { balance, code = "0x", nonce = "0x1" },
            [contract] = new { balance, code = "0x" + bytecode, nonce = "0x1", state = new Dictionary<string, string>() }
        };
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { from = sender, to = contract, gas = $"0x{gasLimit:x}" }, "latest",
            new { tracer = "callTracer", stateOverrides });
        JToken result = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((string?)result["result"]?["error"], Is.EqualTo(expectedError), response);
            Assert.That((string?)result["result"]?["gasUsed"], Is.EqualTo($"0x{gasLimit:x}"), response);
            Assert.That(result["error"], Is.Null, response);
        }
    }

    [Test]
    public async Task Debug_traceCall_named_tracer_ignores_opcode_logger_limit([Values(-1, 1)] long limit)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Osaka.Instance));
        Dictionary<string, object> stateOverrides = new()
        {
            [TestItem.AddressC.ToString()] = new { code = "0x602a60005260206000f3" }
        };
        object call = new { to = TestItem.AddressC.ToString(), gas = "0x186a0" };
        string unlimited = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            call, "latest", new { tracer = "callTracer", stateOverrides });
        string limited = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            call, "latest", new { tracer = "callTracer", limit = JsonSerializer.SerializeToElement(limit), stateOverrides });

        Assert.That(JToken.DeepEquals(JToken.Parse(limited), JToken.Parse(unlimited)), Is.True, limited);
    }

    [TestCase(false, "60006000fd", 21006)]
    [TestCase(true, "60006000fd", 21006)]
    [TestCase(false, "fe", 100000)]
    [TestCase(true, "fe", 100000)]
    public async Task Debug_traceCall_failed_opcode_trace_reports_consumed_gas(bool streamMode, string code, long expectedGas)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Osaka.Instance));
        object? stateOverrides = JsonSerializer.Deserialize<object>(
            $$$"""{"{{{TestItem.AddressC}}}":{"code":"0x{{{code}}}"}}""");

        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { to = TestItem.AddressC.ToString(), gas = "0x186a0" }, "latest", new { streamMode, stateOverrides });

        JToken result = JToken.Parse(response)["result"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That((bool?)result["failed"], Is.True);
            Assert.That((long?)result["gas"], Is.EqualTo(expectedGas));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Debug_traceCall_omits_empty_memory(bool streamMode)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Osaka.Instance));
        object? stateOverrides = JsonSerializer.Deserialize<object>(
            $$$"""{"{{{TestItem.AddressC}}}":{"code":"0x602a60005200"}}""");

        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { to = TestItem.AddressC.ToString(), gas = "0x186a0" }, "latest",
            new { streamMode, enableMemory = true, stateOverrides });

        JArray entries = (JArray)JToken.Parse(response)["result"]!["structLogs"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries[0]["memory"], Is.Null);
            Assert.That(entries[1]["memory"], Is.Null);
            Assert.That(entries[2]["memory"], Is.Null);
            Assert.That((string?)entries[3]["memory"]?[0], Is.EqualTo("0x" + new string('0', 62) + "2a"));
        }
    }

    [TestCase(false, null)]
    [TestCase(true, null)]
    [TestCase(false, "callTracer")]
    [TestCase(true, "callTracer")]
    public async Task Debug_traceCall_rejects_pending(bool useBlockObject, string? tracer)
    {
        using Context ctx = await Context.Create();
        object blockParameter = useBlockObject ? new { blockNumber = "pending" } : "pending";

        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { to = TestItem.AddressC.ToString() }, blockParameter, new { tracer });

        JToken result = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result["result"], Is.Null);
            Assert.That((int?)result["error"]?["code"], Is.EqualTo(ErrorCodes.InvalidInput));
            Assert.That((string?)result["error"]?["message"], Is.EqualTo("tracing on top of pending is not supported"));
        }
    }

    [Test]
    public async Task Debug_traceCall_runs_on_top_of_specified_block()
    {
        using Context ctx = await Context.Create();
        TestRpcBlockchain blockchain = ctx.Blockchain;

        Address address = Build.An.Address.TestObject;
        UInt256 balance = 100.Ether;

        await blockchain.AddFunds(address, balance / 2);
        await blockchain.AddFunds(address, balance / 2);
        Hash256 lastBlockHash = blockchain.BlockTree.Head!.Hash!;

        JsonRpcResponse response = await RpcTest.TestRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { from = $"{address}", to = $"{TestItem.AddressC}", value = balance.ToString("X") },
            $"{lastBlockHash}"
        );

        RpcTest.AssertSuccess(response);
    }

    [Test]
    public async Task Debug_traceCall_validates_requested_chain_id(
        [Values(null, false, true)] bool? mismatch, [Values(null, "callTracer")] string? tracer)
    {
        TestSpecProvider provider = new(Cancun.Instance);
        using Context ctx = await Context.Create(provider);
        ulong requested = mismatch == true ? provider.ChainId + 1 : provider.ChainId;
        Dictionary<string, object> transaction = new() { ["to"] = TestItem.AddressC.ToString(), ["gas"] = "0x186a0" };
        if (mismatch is not null) transaction["chainId"] = "0x" + requested.ToString("x");
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            transaction, "latest", new { tracer });
        JToken json = JToken.Parse(response);
        if (mismatch == true)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(json["error"]?["code"]?.Value<int>(), Is.EqualTo(ErrorCodes.InvalidInput));
                Assert.That(json["error"]?["message"]?.Value<string>(), Is.EqualTo($"chainId does not match node's (have={requested}, want={provider.ChainId})"));
            }
        }
        else
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(json["error"], Is.Null);
                Assert.That(json["result"], Is.Not.Null);
            }
        }
    }

    [TestCase(TxType.SetCode, null, null)]
    [TestCase(TxType.SetCode, null, "0x0")]
    [TestCase(TxType.Blob, "0x0", null)]
    [TestCase(TxType.Blob, "0x0", "0x0")]
    [TestCase(TxType.Blob, "0x1", null)]
    [TestCase(TxType.Blob, "0x1", "0x0")]
    public async Task Debug_traceCall_rejects_transaction_types_before_activation(TxType type, string? blobFeeCap, string? txIndex)
    {
        IReleaseSpec spec = type == TxType.SetCode ? Cancun.Instance : Shanghai.Instance;
        using Context ctx = await Context.Create(new TestSpecProvider(spec));
        Dictionary<string, object> call = new()
        {
            ["to"] = TestItem.AddressC.ToString(),
            ["gas"] = "0x186a0",
            ["type"] = "0x" + ((byte)type).ToString("x")
        };
        if (type == TxType.SetCode)
            call["authorizationList"] = new[] { new { chainId = "0x1", address = TestItem.AddressB.ToString(), nonce = "0x0", yParity = "0x0", r = "0x1", s = "0x1" } };
        else
        {
            call["maxFeePerBlobGas"] = blobFeeCap!;
            call["blobVersionedHashes"] = new[] { "0x01" + new string('0', 62) };
        }

        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", call, "latest", new { txIndex });
        JToken json = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(json["result"], Is.Null, response);
            Assert.That((string?)json["error"]?["message"], Is.EqualTo(TxErrorMessages.InvalidTxType(spec.Name)), response);
            Assert.That((int?)json["error"]?["code"], Is.EqualTo(ErrorCodes.InvalidInput), response);
        }
    }

    private static IEnumerable<TestCaseData> UnsupportedBlockOverrides()
    {
        (string Json, string? Error, int? Code)[] cases =
        [
            ("{\"beaconRoot\":\"0x0000000000000000000000000000000000000000000000000000000000000000\"}", "block override \"beaconRoot\" is not supported for this RPC method", ErrorCodes.InvalidInput),
            ("{\"withdrawals\":[]}", BlockOverride.WithdrawalsNotSupported, ErrorCodes.InvalidParams),
            ("{\"beaconRoot\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"withdrawals\":[]}", BlockOverride.WithdrawalsNotSupported, ErrorCodes.InvalidParams),
            ("{\"beaconRoot\":null,\"withdrawals\":null}", null, null)
        ];
        foreach ((string json, string? error, int? code) in cases)
            foreach (string? tracer in new string?[] { null, "callTracer" })
                yield return new TestCaseData(json, error, code, tracer);
    }

    [TestCaseSource(nameof(UnsupportedBlockOverrides))]
    public async Task Debug_traceCall_validates_unsupported_block_overrides(string overrides, string? expectedError, int? expectedCode, string? tracer)
    {
        using Context ctx = await Context.Create();
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { to = TestItem.AddressC.ToString(), gas = "0x186a0" }, "latest",
            new { tracer, blockOverrides = JsonSerializer.Deserialize<JsonElement>(overrides) });
        JToken json = JToken.Parse(response);
        if (expectedError is null)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(json["error"], Is.Null);
                Assert.That(json["result"], Is.Not.Null);
            }
        }
        else
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(json["error"]?["code"]?.Value<int>(), Is.EqualTo(expectedCode));
                Assert.That(json["error"]?["message"]?.Value<string>(), Is.EqualTo(expectedError));
            }
        }
    }

    [TestCase("0x4a60005260206000f3", "blobBaseFee", "0x9", null)]
    [TestCase("0x4a60005260206000f3", "blobBaseFee", "0x9", "0x0")]
    [TestCase("0x4460005260206000f3", "prevRandao", "0x0000000000000000000000000000000000000000000000000000000000000000", null)]
    [TestCase("0x4460005260206000f3", "prevRandao", "0x0000000000000000000000000000000000000000000000000000000000000000", "0x0")]
    [TestCase("0x4460005260206000f3", "prevRandao", "0x0000000000000000000000000000000000000000000000000000000000000009", null)]
    [TestCase("0x4460005260206000f3", "prevRandao", "0x0000000000000000000000000000000000000000000000000000000000000009", "0x0")]
    public async Task Debug_traceCall_applies_execution_context_override(string code, string field, string value, string? txIndex)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        ctx.Blockchain.BlockTree.Head!.Header.ExcessBlobGas = 0;
        const string address = "0xc200000000000000000000000000000000000000";
        object stateOverrides = JsonSerializer.Deserialize<object>("{\"" + address + "\":{\"code\":\"" + code + "\"}}")!;
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { to = address, gas = "0x186a0" }, "latest",
            new { stateOverrides, blockOverrides = new Dictionary<string, string> { [field] = value }, txIndex });
        JToken result = JToken.Parse(response)["result"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result["failed"]!.Value<bool>(), Is.False);
            Assert.That(ParseReturnValue(response).ToUInt256(), Is.EqualTo(Bytes.FromHexString(value).ToUInt256()));
        }
    }

    [Test]
    public async Task Debug_traceCall_mux_accepts_empty_configuration([Values(null, "null", "{}")] string? config)
    {
        using Context ctx = await Context.Create();
        Dictionary<string, object> options = new() { ["tracer"] = "muxTracer" };
        if (config is not null) options["tracerConfig"] = JsonSerializer.Deserialize<JsonElement>(config);

        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { to = TestItem.AddressC.ToString() }, "latest", options);

        Assert.That(JToken.Parse(response)["result"], Is.EqualTo(JObject.Parse("{}")).Using(JToken.EqualityComparer));
    }

    [Test]
    public async Task Debug_traceCall_mux_matches_individual_native_and_nested_tracers([Values(null, "0x0")] string? txIndex)
    {
        using Context ctx = await Context.Create();
        Dictionary<string, object> children = new()
        {
            ["callTracer"] = new { withLog = true },
            ["prestateTracer"] = new { includeEmpty = true },
            ["4byteTracer"] = new { },
            ["muxTracer"] = new { noopTracer = new { } }
        };
        object stateOverrides = new Dictionary<string, object>
        {
            [TestItem.AddressC.ToString()] = new { code = "0x60006000a000" }
        };
        object transaction = new { to = TestItem.AddressC.ToString(), gas = "0x186a0" };
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", transaction,
            "latest", new { tracer = "muxTracer", tracerConfig = children, stateOverrides, txIndex });
        JToken result = JToken.Parse(response)["result"]!;

        foreach ((string tracer, object tracerConfig) in children)
        {
            string individual = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", transaction,
                "latest", new { tracer, tracerConfig, stateOverrides, txIndex });
            Assert.That(result[tracer], Is.EqualTo(JToken.Parse(individual)["result"]).Using(JToken.EqualityComparer), tracer);
        }
        Assert.That(result["callTracer"]!["logs"]![0]!["index"]!.Value<string>(), Is.EqualTo("0x0"));
    }

    [Test]
    public async Task Debug_traceCall_mux_keeps_each_javascript_engine_selected()
    {
        using Context ctx = await Context.Create();
        const string first = "{step:function(log){this.address=toHex(log.contract.getAddress());},fault:function(){},result:function(ctx){return {address:this.address,from:toHex(ctx.from),value:ctx.value.toString(10)};}}";
        const string second = "{setup:function(config){this.config=JSON.parse(config);},step:function(log){this.balance=log.stack.length() ? log.stack.peek(0).toString(10) : '0';},fault:function(){},result:function(ctx,db){return {balance:this.balance,from:toHex(ctx.from),nonce:db.getNonce(ctx.from),config:this.config};}}";
        Dictionary<string, object> children = new()
        {
            [first] = new { },
            ["muxTracer"] = new Dictionary<string, object> { [second] = new { enabled = true } },
            ["noopTracer"] = new { }
        };
        object stateOverrides = new Dictionary<string, object>
        {
            [TestItem.AddressC.ToString()] = new { code = "0x600100" }
        };
        object transaction = new { to = TestItem.AddressC.ToString(), gas = "0x186a0" };
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", transaction,
            "latest", new { tracer = "muxTracer", tracerConfig = children, stateOverrides });
        JToken result = JToken.Parse(response)["result"]!;

        foreach ((string tracer, object tracerConfig) in children)
        {
            string individual = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", transaction,
                "latest", new { tracer, tracerConfig, stateOverrides });
            Assert.That(result[tracer], Is.EqualTo(JToken.Parse(individual)["result"]).Using(JToken.EqualityComparer), tracer);
        }
    }

    [TestCase("[]", "json: cannot unmarshal array into Go value of type map[string]jsontext.Value")]
    [TestCase("1", "json: cannot unmarshal number into Go value of type map[string]jsontext.Value")]
    [TestCase("true", "json: cannot unmarshal bool into Go value of type map[string]jsontext.Value")]
    [TestCase("\"x\"", "json: cannot unmarshal string into Go value of type map[string]jsontext.Value")]
    [TestCase("{\"prestateTracer\":{\"diffMode\":true,\"includeEmpty\":true}}", "cannot use diffMode with includeEmpty")]
    [TestCase("{\"\":{}}", "SyntaxError: SyntaxError: (anonymous): Line 1:3 Unexpected token )")]
    public async Task Debug_traceCall_mux_rejects_invalid_configuration(string config, string message)
    {
        using Context ctx = await Context.Create();
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { to = TestItem.AddressC.ToString() }, "latest",
            new { tracer = "muxTracer", tracerConfig = JsonSerializer.Deserialize<JsonElement>(config) });
        JToken json = JToken.Parse(response);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(json["result"], Is.Null);
            Assert.That(json["error"]!["code"]!.Value<int>(), Is.EqualTo(ErrorCodes.InvalidInput));
            Assert.That(json["error"]!["message"]!.Value<string>(), Is.EqualTo(message));
        }
    }

    [Test]
    [NonParallelizable]
    public async Task Debug_traceCall_mux_disposes_started_native_children_when_initialization_fails()
    {
        using Context ctx = await Context.Create();
        int disposed = 0;
        string name = "mux_disposal_" + Guid.NewGuid().ToString("N");
        Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.GethLikeNativeTracerFactory.RegisterTracer(name,
            (options, _, _, _) => new MuxDisposalTracer(options, () => disposed++));
        Dictionary<string, object> children = new()
        {
            [name] = new { },
            ["prestateTracer"] = new { diffMode = true, includeEmpty = true }
        };

        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { to = TestItem.AddressC.ToString() }, "latest", new { tracer = "muxTracer", tracerConfig = children });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(JToken.Parse(response)["error"]!["message"]!.Value<string>(), Is.EqualTo("cannot use diffMode with includeEmpty"));
            Assert.That(disposed, Is.EqualTo(1));
        }
    }

    private sealed class MuxDisposalTracer(GethTraceOptions options, Action disposed, bool failResult = false)
        : Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.GethLikeNativeTxTracer(options)
    {
        public override GethLikeTxTrace BuildResult() => failResult
            ? throw new InvalidDataException("mux child result failure")
            : base.BuildResult();

        public override void Dispose() => disposed();
    }

    [Test]
    [NonParallelizable]
    public async Task Debug_traceCall_mux_releases_children_when_a_child_fails([Values] bool resultFailure)
    {
        using Context ctx = await Context.Create();
        int disposed = 0;
        string first = "mux_disposal_first_" + Guid.NewGuid().ToString("N");
        string failing = "mux_disposal_failing_" + Guid.NewGuid().ToString("N");
        Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.GethLikeNativeTracerFactory.RegisterTracer(first,
            (options, _, _, _) => new MuxDisposalTracer(options, () => disposed++));
        Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.GethLikeNativeTracerFactory.RegisterTracer(failing,
            (options, _, _, _) => new MuxDisposalTracer(options, () => disposed++, failResult: true));
        Dictionary<string, object> children = new()
        {
            [first] = new { },
            [resultFailure ? failing : "prestateTracer"] = resultFailure ? new { } : new { diffMode = true, includeEmpty = true }
        };

        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { to = TestItem.AddressC.ToString() }, "latest", new { tracer = "muxTracer", tracerConfig = children });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(JToken.Parse(response)["error"], Is.Not.Null);
            Assert.That(disposed, Is.EqualTo(resultFailure ? 2 : 1));
        }
    }

    [Test]
    public async Task Debug_traceCall_rejects_prestate_diff_with_include_empty([Values(null, "0x0")] string? txIndex)
    {
        using Context ctx = await Context.Create();
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { to = TestItem.AddressB.ToString() }, "latest",
            new { tracer = "prestateTracer", tracerConfig = new { diffMode = true, includeEmpty = true }, txIndex });
        JToken error = JToken.Parse(response)["error"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error["code"]!.Value<int>(), Is.EqualTo(ErrorCodes.InvalidInput));
            Assert.That(error["message"]!.Value<string>(), Is.EqualTo("cannot use diffMode with includeEmpty"));
        }
    }

    [Test]
    public async Task Debug_traceCall_rejects_conflicting_storage_overrides([Values(null, "callTracer")] string? tracer)
    {
        using Context ctx = await Context.Create();
        const string address = "0xc200000000000000000000000000000000000000";
        object stateOverrides = JsonSerializer.Deserialize<object>("{\"" + address + "\":{\"state\":{},\"stateDiff\":{}}}")!;
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { to = address }, "latest", new { stateOverrides, tracer });
        JToken error = JToken.Parse(response)["error"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error["code"]!.Value<int>(), Is.EqualTo(ErrorCodes.InvalidInput));
            Assert.That(error["message"]!.Value<string>(), Is.EqualTo($"account {address} has both 'state' and 'stateDiff'"));
        }
    }

    [TestCase(false, false, false, TestName = "Debug_traceCall_without_gas_pricing_uses_zero_base_fee")]
    [TestCase(false, false, true, TestName = "Debug_traceCall_without_gas_pricing_ignores_base_fee_override")]
    [TestCase(true, true, false, TestName = "Debug_traceCall_with_max_fee_uses_live_base_fee")]
    [TestCase(true, true, true, TestName = "Debug_traceCall_with_max_fee_uses_base_fee_override")]
    public async Task Debug_traceCall_uses_expected_base_fee(bool includeMaxFeePerGas, bool includeMaxPriorityFeePerGas, bool overrideBaseFee)
    {
        OverridableReleaseSpec releaseSpec = new(London.Instance) { Eip1559TransitionBlock = 1 };
        using Context ctx = await Context.Create(new TestSpecProvider(releaseSpec));

        UInt256 baseFee = ctx.Blockchain.BlockTree.Head!.Header.BaseFeePerGas;
        Assert.That(baseFee, Is.Not.EqualTo(UInt256.Zero));

        Address sender = Build.An.Address.TestObject;
        Dictionary<string, object?> transaction = new()
        {
            ["from"] = $"{sender}",
            ["data"] = "0x4860005260206000f3"
        };
        if (includeMaxFeePerGas)
            transaction["maxFeePerGas"] = "0x100000000";
        if (includeMaxPriorityFeePerGas)
            transaction["maxPriorityFeePerGas"] = "0x1";

        object stateOverride = JsonSerializer.Deserialize<object>(
            "{\"" + sender + "\":{\"balance\":\"0x56BC75E2D63100000\"}}")!;
        object? options = overrideBaseFee
            ? new
            {
                stateOverrides = stateOverride,
                blockOverrides = new { baseFeePerGas = "0x100" }
            }
            : new { stateOverrides = stateOverride };

        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            transaction, null, options);

        JToken result = JToken.Parse(response)["result"]!;
        UInt256 expectedBaseFee = !includeMaxFeePerGas && !includeMaxPriorityFeePerGas
            ? UInt256.Zero
            : overrideBaseFee
                ? (UInt256)0x100
                : baseFee;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result["failed"]?.Value<bool>(), Is.False);
            Assert.That(ParseReturnValue(response).ToUInt256(), Is.EqualTo(expectedBaseFee));
        }
    }

    [Test]
    public async Task Debug_traceCall_rejects_conflicting_storage_before_fee_validation(
        [Values] bool streaming, [Values] bool mixedFees, [Values] bool explicitType)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = streaming }).Build();
        const string address = "0x000000000000000000000000000000000000dead";
        Dictionary<string, object> call = new()
        {
            ["from"] = TestItem.AddressB.ToString(),
            ["to"] = address,
            ["gas"] = "0x186a0"
        };
        if (mixedFees)
        {
            call["gasPrice"] = "0x1";
            call["maxFeePerGas"] = "0x2";
        }
        if (explicitType) call["type"] = "0x2";
        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceCall", call, "latest",
            new { stateOverrides = new Dictionary<string, object> { [address] = new { code = "0x00", state = new { }, stateDiff = new { } } } });
        JToken json = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int?)json["error"]?["code"], Is.EqualTo(-32000), response);
            Assert.That((string?)json["error"]?["message"], Is.EqualTo("account 0x000000000000000000000000000000000000dEaD has both 'state' and 'stateDiff'"), response);
            Assert.That(json["result"], Is.Null, response);
        }
    }

    [Test]
    public async Task Debug_traceCall_rejects_precompile_move_collision_before_fee_validation([Values] bool streaming)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = streaming }).Build();
        Block block = chain.BlockTree.Head!;
        BlockHeader parent = chain.BlockTree.FindHeader(block.ParentHash!)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.IsGenesis, Is.False);
            Assert.That(parent, Is.Not.Null);
            Assert.That(chain.Bridge.HasStateForBlock(block.Header), Is.True);
            Assert.That(chain.Bridge.HasStateForBlock(parent), Is.True);
        }

        const string source = "0x0000000000000000000000000000000000000001";
        const string destination = "0x0000000000000000000000000000000000000002";
        object options = new
        {
            stateOverrides = new Dictionary<string, object>
            {
                [source] = new { movePrecompileToAddress = destination },
                [destination] = new { }
            }
        };
        Dictionary<string, object> mixedFees = new()
        {
            ["from"] = TestItem.AddressB.ToString(),
            ["to"] = TestItem.AddressD.ToString(),
            ["gas"] = "0x186a0",
            ["gasPrice"] = "0x1",
            ["maxFeePerGas"] = "0x2"
        };
        Dictionary<string, object> validFees = new(mixedFees);
        validFees.Remove("maxFeePerGas");
        string selector = block.Hash!.ToString();
        string feeResponse = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceCall", mixedFees, selector);
        AssertTraceCallInputError(feeResponse, "both gasPrice and (maxFeePerGas or maxPriorityFeePerGas) specified");

        string collisionResponse = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceCall", validFees, selector, options);
        string expectedCollision = $"account {destination} is already overridden";
        AssertTraceCallInputError(collisionResponse, expectedCollision);

        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceCall", mixedFees, selector, options);
        AssertTraceCallInputError(response, expectedCollision);
    }

    [Test]
    public async Task Debug_traceCall_validates_fees_with_null_account_override(
        [Values] bool streaming, [Values("null", "{}")] string accountOverride, [Values(null, "0x0")] string? txIndex)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = streaming }).Build();
        object options = new
        {
            stateOverrides = new Dictionary<string, JsonElement>
            {
                ["0x0000000000000000000000000000000000000010"] = JsonSerializer.Deserialize<JsonElement>(accountOverride)
            },
            txIndex
        };

        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceCall",
            new { to = TestItem.AddressD.ToString(), gasPrice = "0x1", maxFeePerGas = "0x2" }, "latest", options);

        AssertTraceCallInputError(response, "both gasPrice and (maxFeePerGas or maxPriorityFeePerGas) specified");
    }

    private static void AssertTraceCallInputError(string response, string message)
    {
        JToken json = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int?)json["error"]?["code"], Is.EqualTo(ErrorCodes.InvalidInput), response);
            Assert.That((string?)json["error"]?["message"], Is.EqualTo(message), response);
            Assert.That(json["result"], Is.Null, response);
            Assert.That(json["error"]?["data"], Is.Null, response);
            Assert.That((int?)json["id"], Is.EqualTo(67), response);
        }
    }

    [Test]
    public async Task Debug_traceCall_orders_override_validation(
        [Values] bool streaming, [Values] bool reverse,
        [Values("move-before-storage", "storage-before-move", "source-before-destination", "move-before-own-storage")] string scenario)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = streaming }).Build();
        Address source = scenario == "source-before-destination" ? Address.Zero : new("0x0000000000000000000000000000000000000001");
        Address destination = new("0x0000000000000000000000000000000000000002");
        Dictionary<string, object> sourceOverride = new() { ["movePrecompileToAddress"] = destination.ToString() };
        if (scenario == "move-before-own-storage")
        {
            sourceOverride["state"] = new { };
            sourceOverride["stateDiff"] = new { };
        }
        Dictionary<string, object> overrides = new()
        {
            [source.ToString()] = sourceOverride,
            [destination.ToString()] = new { }
        };
        if (scenario is "move-before-storage" or "storage-before-move")
            overrides[(scenario == "move-before-storage" ? new Address("0x0000000000000000000000000000000000000003") : Address.Zero).ToString()] = new { state = new { }, stateDiff = new { } };
        if (reverse)
        {
            KeyValuePair<string, object>[] entries = new KeyValuePair<string, object>[overrides.Count];
            ((ICollection<KeyValuePair<string, object>>)overrides).CopyTo(entries, 0);
            Array.Reverse(entries);
            overrides.Clear();
            foreach (KeyValuePair<string, object> entry in entries) overrides.Add(entry.Key, entry.Value);
        }
        string expected = scenario switch
        {
            "source-before-destination" => $"Account {source} is not a precompile",
            "storage-before-move" => $"account {Address.Zero} has both 'state' and 'stateDiff'",
            _ => $"account {destination} is already overridden"
        };
        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceCall",
            new { to = TestItem.AddressC.ToString(), gasPrice = "0x1", maxFeePerGas = "0x2" }, "latest",
            new { stateOverrides = overrides });
        AssertTraceCallInputError(response, expected);
    }

    [Test]
    public async Task Debug_traceCall_uses_the_trace_repository_for_custom_precompile_preflight(
        [Values] bool streaming, [Values] bool indexed)
    {
        Address source = new("0x0000000000000000000000000000000000010001");
        Address destination = new("0x0000000000000000000000000000000000020000");
        TestSpecProvider specs = new(Berlin.Instance)
        {
            ForkOnBlockNumber = 100,
            NextForkSpec = new ExtraPrecompileSpec(source)
        };
        IPrecompileProvider provider = Substitute.For<IPrecompileProvider>();
        Dictionary<AddressAsKey, CodeInfo> precompiles = new(new EthereumPrecompileProvider().GetPrecompiles())
        {
            [source] = new CodeInfo(IdentityPrecompile.Instance)
        };
        provider.GetPrecompiles().Returns(precompiles.ToFrozenDictionary());
        ConcurrentBag<PrecompileRepositoryProbe> repositories = [];
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = streaming }).Build(builder => builder
                .AddSingleton<ISpecProvider>(specs)
                .AddSingleton<IPrecompileProvider>(provider)
                .AddDecorator<ICodeInfoRepository>((_, inner) =>
                {
                    PrecompileRepositoryProbe probe = new(inner, source);
                    repositories.Add(probe);
                    return probe;
                })
                .AddDecorator<IOverridableCodeInfoRepository>((_, inner) =>
                {
                    OverridablePrecompileRepositoryProbe probe = new(inner, source);
                    repositories.Add(probe);
                    return probe;
                }));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(specs.GetSpec((ForkActivation)100).IsPrecompile(source), Is.True);
            Assert.That(chain.SpecProvider.GetSpec((ForkActivation)100).IsPrecompile(source), Is.True,
                "the test-chain spec wrapper must retain the custom precompile set");
            Assert.That(chain.SpecProvider.GetSpec((ForkActivation)1).IsPrecompile(source), Is.False);
        }
        IDebugRpcModule module = chain.DebugRpcModule;
        Hash256 originalRoot = chain.BlockTree.Head!.StateRoot!;
        Dictionary<string, object> mixedFees = new()
        {
            ["from"] = TestItem.AddressB.ToString(),
            ["to"] = destination.ToString(),
            ["gas"] = "0x186a0",
            ["gasPrice"] = "0x1",
            ["maxFeePerGas"] = "0x2"
        };
        Dictionary<string, object> collision = new()
        {
            [source.ToString()] = new { movePrecompileToAddress = destination.ToString() },
            [destination.ToString()] = new { }
        };
        string inactive = await RpcTest.TestSerializedRequest(module, "debug_traceCall", mixedFees, "latest",
            new { stateOverrides = collision });
        AssertTraceCallInputError(inactive, $"Account {source} is not a precompile");
        object collisionOptions = new
        {
            txIndex = indexed ? "0x0" : null,
            blockOverrides = new { number = "0x64" },
            stateOverrides = collision
        };
        string rejected = await RpcTest.TestSerializedRequest(module, "debug_traceCall", mixedFees, "latest", collisionOptions);
        AssertTraceCallInputError(rejected, $"account {destination} is already overridden");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(CountRepositoryReads(repositories).Precompile, Is.GreaterThan(0));
            Assert.That(CountRepositoryReads(repositories).Code, Is.Zero, "preflight must not use the stateful lookup");
            Assert.That(chain.BlockTree.Head.StateRoot, Is.EqualTo(originalRoot));
        }

        object validMove = new
        {
            txIndex = indexed ? "0x0" : null,
            blockOverrides = new { number = "0x64" },
            stateOverrides = new Dictionary<string, object> { [source.ToString()] = new { movePrecompileToAddress = destination.ToString() } }
        };
        string feeOnly = await RpcTest.TestSerializedRequest(module, "debug_traceCall", mixedFees, "latest", validMove);
        AssertTraceCallInputError(feeOnly, "both gasPrice and (maxFeePerGas or maxPriorityFeePerGas) specified");
        Assert.That(CountRepositoryReads(repositories).Code, Is.Zero);

        string success = await RpcTest.TestSerializedRequest(module, "debug_traceCall",
            new { from = TestItem.AddressB.ToString(), to = destination.ToString(), gas = "0x186a0", data = "0x010203" }, "latest", validMove);
        JToken json = JToken.Parse(success);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(json["error"], Is.Null, success);
            Assert.That((bool?)json["result"]?["failed"], Is.False, success);
            Assert.That((string?)json["result"]?["returnValue"], Is.EqualTo("0x010203"), success);
            int moves = 0;
            int sourceReads = 0;
            foreach (PrecompileRepositoryProbe probe in repositories)
            {
                if (probe is OverridablePrecompileRepositoryProbe overridable)
                {
                    moves += overridable.Moves;
                    Assert.That(probe.SourceCodeReads, Is.EqualTo(overridable.Moves));
                    if (overridable.Moves != 0)
                        Assert.That(probe.PrecompileReads, Is.GreaterThan(0),
                            "preflight must use the same decorator as override application");
                }
                else
                    sourceReads += probe.SourceCodeReads;
            }
            Assert.That(sourceReads, Is.EqualTo(2),
                "application must retain its source-check and move lookups");
            Assert.That(moves, Is.GreaterThan(0));
            Assert.That(chain.BlockTree.Head.StateRoot, Is.EqualTo(originalRoot));
        }
        int readsAfterMove = CountRepositoryReads(repositories).Code;
        string afterReset = await RpcTest.TestSerializedRequest(module, "debug_traceCall", mixedFees, "latest", collisionOptions);
        AssertTraceCallInputError(afterReset, $"account {destination} is already overridden");
        Assert.That(CountRepositoryReads(repositories).Code, Is.EqualTo(readsAfterMove));
    }

    private static (int Precompile, int Code) CountRepositoryReads(IEnumerable<PrecompileRepositoryProbe> repositories)
    {
        int precompile = 0;
        int code = 0;
        foreach (PrecompileRepositoryProbe probe in repositories)
        {
            precompile += probe.PrecompileReads;
            code += probe.SourceCodeReads;
        }
        return (precompile, code);
    }

    private sealed class ExtraPrecompileSpec(Address source) : ReleaseSpecDecorator(Berlin.Instance), IReleaseSpec
    {
        FrozenSet<AddressAsKey> IReleaseSpec.Precompiles { get; } =
            new HashSet<AddressAsKey>(((IReleaseSpec)Berlin.Instance).Precompiles) { source }.ToFrozenSet();
        bool IReleaseSpec.IsPrecompile(Address address) => address == source || ((IReleaseSpec)Berlin.Instance).IsPrecompile(address);
    }

    private class PrecompileRepositoryProbe(ICodeInfoRepository inner, Address source) : ICodeInfoRepository
    {
        public int PrecompileReads;
        public int SourceCodeReads;
        public bool IsCodeOverridable => inner.IsCodeOverridable;
        public IPrecompile? GetPrecompile(Address address, IReleaseSpec spec)
        {
            if (address == source) Interlocked.Increment(ref PrecompileReads);
            return inner.GetPrecompile(address, spec);
        }
        public CodeInfo GetCachedCodeInfo(Address address, bool followDelegation, IReleaseSpec spec, out Address? delegation)
        {
            if (address == source) Interlocked.Increment(ref SourceCodeReads);
            return inner.GetCachedCodeInfo(address, followDelegation, spec, out delegation);
        }
        public CodeInfo GetDelegatedCodeInfo(Address address, IReleaseSpec spec) => inner.GetDelegatedCodeInfo(address, spec);
        public void InsertCode(ReadOnlyMemory<byte> code, Address address, IReleaseSpec spec) => inner.InsertCode(code, address, spec);
        public void InsertTransientCode(ReadOnlyMemory<byte> code, Address address, IReleaseSpec spec) => inner.InsertTransientCode(code, address, spec);
        public void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec) => inner.SetDelegation(codeSource, authority, spec);
        public bool TryGetDelegation(Address address, IReleaseSpec spec, [NotNullWhen(true)] out Address? delegatedAddress) => inner.TryGetDelegation(address, spec, out delegatedAddress);
    }

    private sealed class OverridablePrecompileRepositoryProbe(IOverridableCodeInfoRepository inner, Address source)
        : PrecompileRepositoryProbe(inner, source), IOverridableCodeInfoRepository
    {
        public int Moves;
        public void SetCodeOverride(IReleaseSpec spec, Address address, CodeInfo code) => inner.SetCodeOverride(spec, address, code);
        public void MovePrecompile(IReleaseSpec spec, Address source, Address destination)
        {
            Interlocked.Increment(ref Moves);
            inner.MovePrecompile(spec, source, destination);
        }
        public void ResetOverrides() => inner.ResetOverrides();
        public void ResetPrecompileOverrides(IReleaseSpec spec) => inner.ResetPrecompileOverrides(spec);
    }

    [Test]
    public async Task Debug_traceCall_selects_block_and_state_before_conflicting_storage(
        [Values("pending", "missing", "unavailable", "unavailable-parent", "unavailable-with-withdrawals")] string selection, [Values] bool streaming, [Values] bool movePrecompile)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = streaming }).Build();
        if (selection is "unavailable" or "unavailable-with-withdrawals") chain.BlockTree.Head!.Header.StateRoot = TestItem.KeccakA;
        if (selection == "unavailable-parent")
        {
            Block parent = chain.BlockTree.Head!;
            await chain.AddBlock();
            parent.Header.StateRoot = TestItem.KeccakA;
        }
        string selector = selection switch
        {
            "pending" => "pending",
            "missing" => TestItem.KeccakA.ToString(),
            _ => "latest"
        };
        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceCall",
            new { to = TestItem.AddressC.ToString(), gas = "0x186a0" }, selector,
            new
            {
                txIndex = selection == "unavailable-parent" ? "0x0" : null,
                blockOverrides = selection == "unavailable-with-withdrawals" ? new { withdrawals = Array.Empty<object>() } : null,
                stateOverrides = new Dictionary<string, object>
                {
                    [TestItem.AddressC.ToString()] = new
                    {
                        state = new { },
                        stateDiff = new { },
                        movePrecompileToAddress = movePrecompile ? TestItem.AddressB.ToString() : null
                    }
                }
            });
        (int code, string message) = selection switch
        {
            "unavailable-with-withdrawals" => (ErrorCodes.InvalidParams, BlockOverride.WithdrawalsNotSupported),
            "pending" => (ErrorCodes.InvalidInput, "tracing on top of pending is not supported"),
            "missing" => (ErrorCodes.ResourceNotFound, "header not found"),
            _ => (ErrorCodes.ResourceUnavailable, "No state available")
        };
        JToken json = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int?)json["error"]?["code"], Is.EqualTo(code), response);
            Assert.That((string?)json["error"]?["message"], Does.StartWith(message), response);
            Assert.That(json["result"], Is.Null, response);
        }
    }

    [Test]
    public async Task Debug_traceCall_accepts_single_storage_override([Values] bool streaming, [Values("state", "stateDiff")] string field)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = streaming }).Build();
        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceCall",
            new { from = TestItem.AddressB.ToString(), to = TestItem.AddressC.ToString(), gas = "0x186a0" }, "latest",
            new { stateOverrides = new Dictionary<string, object> { [TestItem.AddressC.ToString()] = new Dictionary<string, object> { [field] = new { }, ["code"] = "0x00" } } });
        JToken json = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(json["error"], Is.Null, response);
            Assert.That((bool?)json["result"]?["failed"], Is.False, response);
        }
    }

    [TestCase(
        "Nonce override doesn't cause failure",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000"}""",
        """{"0x7f554713be84160fdf0178cc8df86f5aabd33397":{"nonce":"0x123"}}"""
    )]
    [TestCase(
        "Uses account balance from state override",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000","value":"0x100"}""",
        """{"0x7f554713be84160fdf0178cc8df86f5aabd33397":{"balance":"0x100"}}"""
    )]
    [TestCase(
        "Executes code from state override",
        """{"from":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","to":"0xc200000000000000000000000000000000000000","input":"0xf8b2cb4f000000000000000000000000b7705ae4c6f81b66cdb323c65f4e8133690fc099"}""",
        """{"0xc200000000000000000000000000000000000000":{"code":"0x608060405234801561001057600080fd5b506004361061002b5760003560e01c8063f8b2cb4f14610030575b600080fd5b61004a600480360381019061004591906100e4565b610060565b604051610057919061012a565b60405180910390f35b60008173ffffffffffffffffffffffffffffffffffffffff16319050919050565b600080fd5b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b60006100b182610086565b9050919050565b6100c1816100a6565b81146100cc57600080fd5b50565b6000813590506100de816100b8565b92915050565b6000602082840312156100fa576100f9610081565b5b6000610108848285016100cf565b91505092915050565b6000819050919050565b61012481610111565b82525050565b600060208201905061013f600083018461011b565b9291505056fea2646970667358221220172c443a163d8a43e018c339d1b749c312c94b6de22835953d960985daf228c764736f6c63430008120033"}}""",
        "00000000000000000000000000000000000000000000003635c9adc5de9f09e5"
    )]
    [TestCase(
        "Executes precompile using overridden address",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000","input":"0xB6E16D27AC5AB427A7F68900AC5559CE272DC6C37C82B3E052246C82244C50E4000000000000000000000000000000000000000000000000000000000000001C7B8B1991EB44757BC688016D27940DF8FB971D7C87F77A6BC4E938E3202C44037E9267B0AEAA82FA765361918F2D8ABD9CDD86E64AA6F2B81D3C4E0B69A7B055"}""",
        """{"0x0000000000000000000000000000000000000001":{"movePrecompileToAddress":"0xc200000000000000000000000000000000000000", "code": "0x"}}""",
        "000000000000000000000000b7705ae4c6f81b66cdb323c65f4e8133690fc099"
    )]
    public async Task Debug_traceCall_with_state_override(string name, string transactionJson, string stateOverrideJson, string? expectedValue = null)
    {
        object? transaction = JsonSerializer.Deserialize<object>(transactionJson);
        object? stateOverride = JsonSerializer.Deserialize<object>(stateOverrideJson);

        using Context ctx = await Context.Create();

        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            transaction, null, new { stateOverrides = stateOverride }
        );

        JToken result = JToken.Parse(response)["result"]!;
        Assert.That(((bool)result["failed"]!), Is.False, $"trace for case '{name}' must succeed");

        if (expectedValue is not null)
        {
            byte[] returnValueBytes = Bytes.FromHexString((string)result["returnValue"]!);
            Assert.That(returnValueBytes.ToHexString(), Is.EqualTo(expectedValue));
        }
    }

    // EIP-8151: ecRecover reads the recovered account, so the prestate holds it wherever movePrecompileToAddress put
    // ecRecover, and not for a call to 0x01 that runs another precompile.
    [TestCase(
        """{"0x0000000000000000000000000000000000000001":{"movePrecompileToAddress":"0xc200000000000000000000000000000000000000", "code": "0x"}}""",
        "0xc200000000000000000000000000000000000000", true,
        TestName = "Debug_traceCall_prestate_follows_a_moved_ecrecover(ecRecover moved away from 0x01)")]
    [TestCase(
        """{"0x0000000000000000000000000000000000000002":{"movePrecompileToAddress":"0x0000000000000000000000000000000000000001"}}""",
        "0x0000000000000000000000000000000000000001", false,
        TestName = "Debug_traceCall_prestate_follows_a_moved_ecrecover(SHA-256 moved onto 0x01)")]
    public async Task Debug_traceCall_prestate_follows_a_moved_ecrecover(string stateOverrideJson, string to, bool includesRecovered)
    {
        const string recovered = "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099";
        using Context ctx = await Context.Create(new TestSpecProvider(new OverridableReleaseSpec(Osaka.Instance) { IsEip8151Enabled = true }));

        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new
            {
                from = "0x7f554713be84160fdf0178cc8df86f5aabd33397",
                to,
                input = "0xB6E16D27AC5AB427A7F68900AC5559CE272DC6C37C82B3E052246C82244C50E4000000000000000000000000000000000000000000000000000000000000001C7B8B1991EB44757BC688016D27940DF8FB971D7C87F77A6BC4E938E3202C44037E9267B0AEAA82FA765361918F2D8ABD9CDD86E64AA6F2B81D3C4E0B69A7B055"
            },
            null,
            new { tracer = "prestateTracer", stateOverrides = JsonSerializer.Deserialize<object>(stateOverrideJson) });

        JObject prestate = (JObject)JToken.Parse(response)["result"]!;
        Assert.That(prestate.ContainsKey(recovered), Is.EqualTo(includesRecovered), response);
    }

    // Contract: GAS PUSH1 0 MSTORE PUSH1 32 PUSH1 0 RETURN
    // Returns gas available at start of execution as a 32-byte uint256.
    private const string GasReturnContractAddress = "0xc200000000000000000000000000000000000000";
    private static object GasReturnContractStateOverride() => JsonSerializer.Deserialize<object>(
        $$$"""{"{{{GasReturnContractAddress}}}":{"code":"0x5a60005260206000f3"}}""")!;

    [Test]
    public async Task Debug_traceCall_caps_gas_to_gas_cap()
    {
        using Context ctx = await Context.Create();
        ulong gasCap = 50_000;
        IJsonRpcConfig config = ctx.Blockchain.Container.Resolve<IJsonRpcConfig>();
        config.GasCap = gasCap;

        // Request 100K gas — should be capped to 50K by GasCap
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { to = GasReturnContractAddress, gas = "0x186A0" },
            null,
            new { stateOverrides = GasReturnContractStateOverride() }
        );

        ulong gasAvailable = (ulong)ParseReturnValue(response).ToUInt256();
        Assert.That(gasAvailable, Is.LessThan(gasCap));
        Assert.That(gasAvailable, Is.GreaterThan(0UL));
    }

    [Test]
    public async Task Debug_traceCall_without_gas_defaults_to_gas_cap_not_block_gas_limit()
    {
        using Context ctx = await Context.Create();

        ulong blockGasLimit = ctx.Blockchain.BlockTree.Head!.Header.GasLimit;
        ulong gasCap = blockGasLimit * 10;
        IJsonRpcConfig config = ctx.Blockchain.Container.Resolve<IJsonRpcConfig>();
        config.GasCap = gasCap;

        string omittedGasResponse = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { to = GasReturnContractAddress },
            null,
            new { stateOverrides = GasReturnContractStateOverride() }
        );

        string explicitGasCapResponse = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { to = GasReturnContractAddress, gas = $"0x{gasCap:x}" },
            null,
            new { stateOverrides = GasReturnContractStateOverride() }
        );

        UInt256 omittedGasAvailable = ParseReturnValue(omittedGasResponse).ToUInt256();
        UInt256 explicitGasCapAvailable = ParseReturnValue(explicitGasCapResponse).ToUInt256();

        Assert.That(omittedGasAvailable, Is.EqualTo(explicitGasCapAvailable));
        Assert.That(omittedGasAvailable > (UInt256)blockGasLimit, Is.True);
    }

    [Test]
    public async Task Debug_traceCall_with_zero_gas_keeps_literal_zero_gas_semantics()
    {
        using Context ctx = await Context.Create();
        ulong gasCap = 50_000;
        IJsonRpcConfig config = ctx.Blockchain.Container.Resolve<IJsonRpcConfig>();
        config.GasCap = gasCap;

        // No state override needed: tx fails at intrinsic-gas validation before the EVM runs.
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { to = GasReturnContractAddress, gas = "0x0" }
        );

        JToken result = JToken.Parse(response)["result"]!;
        Assert.That(result["failed"]?.Value<bool>(), Is.True);
        Assert.That(result["error"]?.Value<string>(), Does.Contain("intrinsic gas too low"));
        Assert.That(result["gas"]?.Value<long>(), Is.EqualTo(0));
    }

    private static byte[] ParseReturnValue(string responseJson)
    {
        JToken result = JToken.Parse(responseJson)["result"]!;
        return Bytes.FromHexString((string)result["returnValue"]!);
    }

    [TestCase(
        "When balance is overridden",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xbe5c953dd0ddb0ce033a98f36c981f1b74d3b33f","value":"0x100"}""",
        """{"0x7f554713be84160fdf0178cc8df86f5aabd33397":{"balance":"0x100"}}"""
    )]
    [TestCase(
        "When address code is overridden",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000","input":"0xf8b2cb4f000000000000000000000000b7705ae4c6f81b66cdb323c65f4e8133690fc099"}""",
        """{"0xc200000000000000000000000000000000000000":{"code":"0x608060405234801561001057600080fd5b506004361061002b5760003560e01c8063f8b2cb4f14610030575b600080fd5b61004a600480360381019061004591906100e4565b610060565b604051610057919061012a565b60405180910390f35b60008173ffffffffffffffffffffffffffffffffffffffff16319050919050565b600080fd5b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b60006100b182610086565b9050919050565b6100c1816100a6565b81146100cc57600080fd5b50565b6000813590506100de816100b8565b92915050565b6000602082840312156100fa576100f9610081565b5b6000610108848285016100cf565b91505092915050565b6000819050919050565b61012481610111565b82525050565b600060208201905061013f600083018461011b565b9291505056fea2646970667358221220172c443a163d8a43e018c339d1b749c312c94b6de22835953d960985daf228c764736f6c63430008120033"}}"""
    )]
    [TestCase(
        "When precompile address is changed",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000","input":"0xB6E16D27AC5AB427A7F68900AC5559CE272DC6C37C82B3E052246C82244C50E4000000000000000000000000000000000000000000000000000000000000001C7B8B1991EB44757BC688016D27940DF8FB971D7C87F77A6BC4E938E3202C44037E9267B0AEAA82FA765361918F2D8ABD9CDD86E64AA6F2B81D3C4E0B69A7B055"}""",
        """{"0x0000000000000000000000000000000000000001":{"movePrecompileToAddress":"0xc200000000000000000000000000000000000000", "code": "0x"}}"""
    )]
    public async Task Debug_traceCall_with_state_override_does_not_affect_other_calls(string name, string transactionJson, string stateOverrideJson)
    {
        object? transaction = JsonSerializer.Deserialize<object>(transactionJson);
        object? stateOverride = JsonSerializer.Deserialize<object>(stateOverrideJson);

        using Context ctx = await Context.Create();

        string resultOverrideBefore = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", transaction, null, new
        {
            stateOverrides = stateOverride,
            enableMemory = false,
            disableStorage = true,
            disableStack = true,
            tracer = "callTracer",
            tracerConfig = new { withLog = false }
        });

        string resultNoOverride = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", transaction, null, new
        {
            // configuration to minimize number of fields being compared
            enableMemory = false,
            disableStorage = true,
            disableStack = true,
            tracer = "callTracer",
            tracerConfig = new { withLog = false }
        });

        string resultOverrideAfter = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", transaction, null, new
        {
            stateOverrides = stateOverride,
            enableMemory = false,
            disableStorage = true,
            disableStack = true,
            tracer = "callTracer",
            tracerConfig = new { withLog = false }
        });

        Assert.That(JToken.Parse(resultOverrideBefore), Is.EqualTo(JToken.Parse(resultOverrideAfter)).Using(JToken.EqualityComparer));
        Assert.That(JToken.Parse(resultNoOverride), Is.Not.EqualTo(JToken.Parse(resultOverrideAfter)).Using(JToken.EqualityComparer));
    }

    private static IEnumerable<TestCaseData> PrestateFieldOptionCases =>
    [
        new TestCaseData(false, false, false, false, false).SetName("Prestate_includes_code_and_storage"),
        new TestCaseData(false, true, false, false, false).SetName("Prestate_disables_code_only"),
        new TestCaseData(false, false, true, false, false).SetName("Prestate_disables_storage_only"),
        new TestCaseData(false, true, true, false, false).SetName("Prestate_disables_code_and_storage"),
        new TestCaseData(false, false, false, false, true).SetName("Prestate_retains_cleared_storage"),
        new TestCaseData(false, true, false, false, true).SetName("Prestate_retains_cleared_storage_without_code"),
        new TestCaseData(false, false, false, true, false).SetName("Prestate_retains_storage_before_reverted_write"),
        new TestCaseData(false, false, false, true, true).SetName("Prestate_retains_storage_before_reverted_clear"),
        new TestCaseData(true, false, false, false, false).SetName("Prestate_diff_includes_storage_write"),
        new TestCaseData(true, true, false, false, false).SetName("Prestate_diff_includes_storage_write_without_code"),
        new TestCaseData(true, false, false, false, true).SetName("Prestate_diff_includes_storage_clear"),
        new TestCaseData(true, true, false, false, true).SetName("Prestate_diff_includes_storage_clear_without_code"),
        new TestCaseData(true, false, true, false, false).SetName("Prestate_diff_omits_disabled_storage_changes"),
        new TestCaseData(true, false, false, true, false).SetName("Prestate_diff_omits_reverted_storage_write"),
        new TestCaseData(true, false, false, true, true).SetName("Prestate_diff_omits_reverted_storage_clear"),
        new TestCaseData(false, false, false, false, false, """{"disableCode":null,"disableStorage":null}""").SetName("Prestate_null_field_options_default_false"),
        new TestCaseData(false, true, true, false, false, """{"disableCode":true,"disableCode":null,"disableStorage":true,"disableStorage":null}""").SetName("Prestate_null_field_options_preserve_true"),
        new TestCaseData(false, false, false, false, false, """{"disableCode":false,"disableCode":null,"disableStorage":false,"disableStorage":null}""").SetName("Prestate_null_field_options_preserve_false"),
        new TestCaseData(false, true, true, false, false, """{"disableCode":false,"disableCode":null,"disableCode":true,"disableStorage":false,"disableStorage":null,"disableStorage":true}""").SetName("Prestate_field_options_last_true_wins"),
        new TestCaseData(false, false, false, false, false, """{"disableCode":true,"disableCode":null,"disableCode":false,"disableStorage":true,"disableStorage":null,"disableStorage":false}""").SetName("Prestate_field_options_last_false_wins"),
        new TestCaseData(false, true, false, false, false, """{"disableCode":"true","disableCode":null,"disableStorage":"false","disableStorage":null}""").SetName("Prestate_null_string_disableCode_preserves_true_with_storage"),
        new TestCaseData(true, true, true, false, false, """{"diffMode":true,"disableCode":"true","disableCode":null,"disableStorage":"true","disableStorage":null}""").SetName("Prestate_diff_null_field_options_preserve_string_true"),
        new TestCaseData(true, false, false, false, false, """{"diffMode":true,"disableCode":"false","disableCode":null,"disableStorage":"false","disableStorage":null}""").SetName("Prestate_diff_null_field_options_preserve_string_false")
    ];

    [TestCaseSource(nameof(PrestateFieldOptionCases))]
    public async Task Debug_traceCall_prestate_respects_field_options(
        bool diffMode, bool disableCode, bool disableStorage, bool revert, bool clearStorage, string? config = null)
    {
        using Context ctx = await Context.Create();
        string sender = TestItem.AddressA.ToString();
        string contract = TestItem.AddressC.ToString();
        string beneficiary = TestItem.AddressD.ToString();
        const string balance = "0x100000000000000000000";
        const string zeroWord = "0x0000000000000000000000000000000000000000000000000000000000000000";
        const string oneWord = "0x0000000000000000000000000000000000000000000000000000000000000001";
        string code = "0x6000545060" + (clearStorage ? "00" : "01") + "600055" + (revert ? "60006000fd" : "00");
        Dictionary<string, object> overrides = new()
        {
            [sender] = new { balance, nonce = "0x1", code = "0x" },
            [contract] = new { balance, nonce = "0x1", code, state = new Dictionary<string, string> { [zeroWord] = clearStorage ? oneWord : zeroWord } },
            [beneficiary] = new { balance, nonce = "0x1", code = "0x" }
        };

        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { from = sender, to = contract, gas = "0x186a0" }, "latest",
            new
            {
                tracer = "prestateTracer",
                tracerConfig = JsonSerializer.Deserialize<JsonElement>(config ?? JsonSerializer.Serialize(new { diffMode, disableCode, disableStorage })),
                stateOverrides = overrides,
                blockOverrides = new { feeRecipient = beneficiary }
            });

        JObject contractPre = new()
        {
            ["balance"] = balance,
            ["nonce"] = 1,
            ["codeHash"] = Keccak.Compute(Bytes.FromHexString(code)).ToString()
        };
        if (!disableCode) contractPre["code"] = code;
        JObject expected;
        if (diffMode)
        {
            JObject pre = new() { [sender] = new JObject { ["balance"] = balance, ["nonce"] = 1 } };
            JObject post = new() { [sender] = new JObject { ["nonce"] = 2 } };
            if (!disableStorage && !revert)
            {
                if (clearStorage) contractPre["storage"] = new JObject { [zeroWord] = oneWord };
                pre[contract] = contractPre;
                JObject contractPost = [];
                if (!clearStorage) contractPost["storage"] = new JObject { [zeroWord] = oneWord };
                post[contract] = contractPost;
            }
            expected = new JObject { ["pre"] = pre, ["post"] = post };
        }
        else
        {
            if (!disableStorage) contractPre["storage"] = new JObject { [zeroWord] = clearStorage ? oneWord : zeroWord };
            expected = new JObject
            {
                [sender] = new JObject { ["balance"] = balance, ["nonce"] = 1 },
                [contract] = contractPre,
                [beneficiary] = new JObject { ["balance"] = balance, ["nonce"] = 1 }
            };
        }

        JObject result = JObject.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result["error"], Is.Null, response);
            Assert.That(result["result"], Is.EqualTo(expected).Using(JToken.EqualityComparer), response);
        }
    }

    [Test]
    public async Task Debug_traceCall_prestate_missing_account_is_omitted_only_from_diff_pre(
        [Values("balance", "extcodehash", "call", "transaction")] string touch, [Values] bool diffMode)
    {
        using Context ctx = await Context.Create();
        Address missing = new("0x0000000000000000000000000000000badc0ffee");
        string address = missing.ToString();
        string sender = TestItem.AddressA.ToString();
        string contract = TestItem.AddressC.ToString();
        byte[] code = touch switch
        {
            "balance" => Prepare.EvmCode.PushData(missing).Op(Instruction.BALANCE).Done,
            "extcodehash" => Prepare.EvmCode.PushData(missing).Op(Instruction.EXTCODEHASH).Done,
            "call" => Prepare.EvmCode.Call(missing, 50_000).Done,
            _ => [(byte)Instruction.STOP]
        };
        Dictionary<string, object> overrides = new()
        {
            [sender] = new { balance = "0x100000000000000000000", nonce = "0x1", code = "0x" },
            [contract] = new { balance = "0x0", nonce = "0x1", code = code.ToHexString(true) }
        };
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { from = sender, to = touch == "transaction" ? address : contract, gas = "0x186a0", value = "0x0" }, "latest",
            new { tracer = "prestateTracer", tracerConfig = new { diffMode, includeEmpty = !diffMode }, stateOverrides = overrides });

        JObject result = JObject.Parse(response);
        Assert.That(result["error"], Is.Null, response);
        if (diffMode)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result["result"]?["pre"]?[address], Is.Null, response);
                Assert.That(result["result"]?["post"]?[address],
                    Is.EqualTo(new JObject { ["codeHash"] = Hash256.Zero.ToString() }).Using(JToken.EqualityComparer), response);
            }
        }
        else
        {
            Assert.That(result["result"]?[address],
                Is.EqualTo(new JObject { ["balance"] = "0x0" }).Using(JToken.EqualityComparer), response);
        }
    }

    [Test]
    public async Task Debug_traceCall_CREATE_replayed_back_to_back_does_not_throw_code_missing()
    {
        using Context ctx = await Context.Create();

        // Minimal CREATE init code that deploys a 1-byte STOP runtime:
        //   PUSH1 1 PUSH1 12 PUSH1 0 CODECOPY  PUSH1 1 PUSH1 0 RETURN  <runtime=0x00>
        // Picking a non-empty runtime is required so its codeHash != keccak("") and the
        // codeDb is actually consulted on GetCode — that's the path that exposes the bug.
        object? transaction = JsonSerializer.Deserialize<object>(
            """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","data":"0x6001600c60003960016000f300","value":"0x0"}""");

        object? stateOverride = JsonSerializer.Deserialize<object>(
            """{"0x7f554713be84160fdf0178cc8df86f5aabd33397":{"balance":"0xffffffffffff"}}""");

        object tracerOptions = new
        {
            tracer = "prestateTracer",
            tracerConfig = new { diffMode = true },
            stateOverrides = stateOverride
        };

        // Run the same CREATE three times against the same pooled IDebugRpcModule. The bug
        // (filter survives overlay reset across pooled-instance reuse) only manifests from
        // the second call onward, so we run a third to make the regression deterministic
        // rather than relying on internal cache ordering.
        for (int i = 1; i <= 3; i++)
        {
            string result = await RpcTest.TestSerializedRequest(
                ctx.DebugRpcModule, "debug_traceCall", transaction, null, tracerOptions);

            Assert.That(result, Does.Contain("\"code\":\"0x00\""),
                $"call #{i} must complete and report the deployed runtime in post.code — " +
                "the persisted-code hint must not survive overlay reset");
        }
    }

    private const string RevertingContractAddress = "0xc300000000000000000000000000000000000000";

    // Error(string) revert payload for "user error", unpadded, as the execution-apis calltree contract emits it.
    private const string RevertPayload =
        "08c379a0" +
        "0000000000000000000000000000000000000000000000000000000000000020" +
        "000000000000000000000000000000000000000000000000000000000000000a" +
        "75736572206572726f72";

    // PUSH1 0x4e PUSH1 0x0c PUSH1 0 CODECOPY PUSH1 0x4e PUSH1 0 REVERT, then the payload as trailing data.
    private const string RevertingContractCode = "0x604e600c600039604e6000fd" + RevertPayload;

    [Test]
    public async Task Debug_traceCall_with_callTracer_reports_revert_in_the_frame()
    {
        using Context ctx = await Context.Create();

        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { to = RevertingContractAddress, gas = "0x100000" },
            null,
            new
            {
                tracer = "callTracer",
                stateOverrides = new Dictionary<string, object>
                {
                    [RevertingContractAddress] = new { code = RevertingContractCode }
                }
            });

        JToken parsed = JToken.Parse(response);
        Assert.That(parsed["error"], Is.Null, "a revert is a traced result, not a JSON-RPC error");

        JToken frame = parsed["result"]!;
        Assert.Multiple(() =>
        {
            Assert.That((string?)frame["type"], Is.EqualTo("CALL"));
            Assert.That((string?)frame["error"], Is.EqualTo("execution reverted"));
            Assert.That((string?)frame["revertReason"], Is.EqualTo("user error"));
            Assert.That((string?)frame["output"], Is.EqualTo("0x" + RevertPayload));
        });
    }

    [Test]
    public async Task Debug_traceCall_with_callTracer_omits_to_on_failed_top_level_create()
    {
        using Context ctx = await Context.Create();

        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { from = TestItem.AddressA.ToString(), data = "0x60006000fd", gas = "0x100000" },
            null,
            new { tracer = "callTracer" });

        JToken parsed = JToken.Parse(response);
        Assert.That(parsed["error"], Is.Null, "a failed deployment is a traced result, not a JSON-RPC error");

        JToken frame = parsed["result"]!;
        Assert.Multiple(() =>
        {
            Assert.That((string?)frame["type"], Is.EqualTo("CREATE"));
            Assert.That((string?)frame["error"], Is.EqualTo("execution reverted"));
            Assert.That(frame["to"], Is.Null, "a failed CREATE deploys no contract, so `to` must be omitted");
        });
    }

    private sealed class PrefixCountingAdapter(ITransactionProcessorAdapter inner, List<Hash256?> executed) : ITransactionProcessorAdapter
    {
        public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
        {
            executed.Add(transaction.Hash);
            return inner.Execute(transaction, txTracer);
        }

        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) => inner.SetBlockExecutionContext(blockExecutionContext);
        public void PrepareForInclusionCheck(Transaction transaction, ulong stateGasAvailable) => inner.PrepareForInclusionCheck(transaction, stateGasAvailable);
    }
}
