// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.Tracing;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    private static IEnumerable<TestCaseData> BlobFeePaymentCases()
    {
        (int Fee, int Cap)[] fees = [(0, 0), (9, 0), (9, 9), (9, 8)];
        foreach ((int fee, int cap) in fees)
            foreach (string? txIndex in new string?[] { null, "0x0" })
                foreach (bool streamMode in new[] { false, true })
                    yield return new TestCaseData(fee, cap, txIndex, streamMode);
    }

    [TestCaseSource(nameof(BlobFeePaymentCases))]
    public async Task Debug_traceCall_blob_fee_override_applies_to_upfront_payment(int fee, int cap, string? txIndex, bool streamMode)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = streamMode })
            .Build(new TestSpecProvider(Cancun.Instance));
        IDebugRpcModule module = chain.DebugRpcModule;
        chain.BlockTree.Head!.Header.ExcessBlobGas = 0;
        const ulong balance = 0x100000000;
        object stateOverrides = new Dictionary<string, object>
        {
            [TestItem.AddressA.ToString()] = new { balance = "0x100000000" },
            [TestItem.AddressC.ToString()] = new { code = "0x32316000524a60205260406000f3" }
        };
        object canonicalOptions = new { stateOverrides, txIndex };
        string before = await RpcTest.TestSerializedRequest(module, "debug_traceCall", BlobFeeTransaction(9), "latest", canonicalOptions);
        AssertBlobPayment(before, balance - 131072, 1);

        string response = await RpcTest.TestSerializedRequest(module, "debug_traceCall", BlobFeeTransaction(cap), "latest",
            new { stateOverrides, txIndex, blockOverrides = new { blobBaseFee = "0x" + fee.ToString("x") } });
        int effectiveFee = cap == 0 ? 0 : fee;
        if (cap < effectiveFee)
        {
            AssertBlobFeeRpcError(response,
                $"max fee per blob gas less than block blob gas fee: address {TestItem.AddressA.ToString(withEip55Checksum: true)} blobGasFeeCap: {cap}, blobBaseFee: {fee}");
        }
        else
        {
            AssertBlobPayment(response, balance - (ulong)effectiveFee * 131072, (ulong)effectiveFee);
        }

        string after = await RpcTest.TestSerializedRequest(module, "debug_traceCall", BlobFeeTransaction(9), "latest", canonicalOptions);
        AssertBlobPayment(after, balance - 131072, 1);
        string zeroCap = await RpcTest.TestSerializedRequest(module, "debug_traceCall", BlobFeeTransaction(0), "latest", canonicalOptions);
        AssertBlobPayment(zeroCap, balance, 0);
    }

    [Test]
    public async Task Debug_traceCall_blob_fee_override_does_not_affect_indexed_prefix([Values(0, 9)] int fee)
    {
        List<UInt256?> observedFees = [];
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = false })
            .Build(builder => builder.AddSingleton<ISpecProvider>(new TestSpecProvider(Cancun.Instance))
                .AddDecorator<ITransactionProcessorAdapter>((context, inner) =>
                    new BlobFeeObservingAdapter(inner, context.ResolveOptional<GethStyleTracer.TraceCallRequestState>(), observedFees)));
        await AddTraceCallPrefixTransfers(chain, 2);
        IDebugRpcModule module = chain.DebugRpcModule;
        observedFees.Clear();
        string response = await RpcTest.TestSerializedRequest(module, "debug_traceCall",
            new { from = TestItem.AddressA.ToString(), to = TestItem.AddressD.ToString(), gas = "0x186a0" }, "latest",
            new { txIndex = "0x1", blockOverrides = new { blobBaseFee = "0x" + fee.ToString("x") } });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(JToken.Parse(response)["result"]!["failed"]!.Value<bool>(), Is.False, response);
            Assert.That(observedFees, Is.EqualTo(new UInt256?[] { null, (UInt256)fee }));
        }
    }

    private sealed class BlobFeeObservingAdapter(ITransactionProcessorAdapter inner,
        GethStyleTracer.TraceCallRequestState? request, List<UInt256?> observedFees) : ITransactionProcessorAdapter
    {
        public TransactionResult Execute(Transaction transaction, ITxTracer tracer)
        {
            if (request is not null) observedFees.Add(request.BlobBaseFee);
            return inner.Execute(transaction, tracer);
        }

        public void SetBlockExecutionContext(in BlockExecutionContext context) => inner.SetBlockExecutionContext(context);
    }

    [TestCase("0x01", false, "blob versioned hash must be 32 bytes")]
    [TestCase("0x02", false, "blob versioned hash version must be 0x01")]
    [TestCase("0x01", true, "maxFeePerGas (1) < maxPriorityFeePerGas (2)")]
    public async Task Debug_traceCall_zero_blob_cap_preserves_other_validation(string hashPrefix, bool conflictingFees, string expectedError)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        string hash = hashPrefix == "0x02" || conflictingFees ? hashPrefix + new string('0', 62) : hashPrefix;
        Dictionary<string, object> transaction = new()
        {
            ["to"] = TestItem.AddressC.ToString(),
            ["maxFeePerBlobGas"] = "0x0",
            ["blobVersionedHashes"] = new[] { hash }
        };
        if (conflictingFees)
        {
            transaction["maxPriorityFeePerGas"] = "0x2";
            transaction["maxFeePerGas"] = "0x1";
        }
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", transaction, "latest");
        Assert.That(JToken.Parse(response)["error"]!["message"]!.Value<string>(), Is.EqualTo(expectedError));
    }

    [Test]
    public async Task Debug_traceCall_blob_fee_rejection_preserves_execution_fee_order(
        [Values(null, "callTracer", "{step:function(){},fault:function(){},result:function(){return {};}}")] string? tracer,
        [Values] bool streamMode, [Values] bool executionFeeTooLow, [Values(null, "0x0")] string? txIndex,
        [Values] bool overrideBlobFee)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = streamMode })
            .Build(new TestSpecProvider(Cancun.Instance));
        chain.BlockTree.Head!.Header.ExcessBlobGas = overrideBlobFee ? 0UL : 7_500_000UL;
        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceCall",
            BlobFeeTransaction(8, executionFeeTooLow ? 1 : 0), "latest", new
            {
                tracer,
                txIndex,
                stateOverrides = new Dictionary<string, object> { [TestItem.AddressA.ToString()] = new { balance = "0x100000000" } },
                blockOverrides = new { blobBaseFee = overrideBlobFee ? "0x9" : null, baseFeePerGas = "0x2" }
            });
        string address = TestItem.AddressA.ToString(withEip55Checksum: true);
        AssertBlobFeeRpcError(response, executionFeeTooLow
            ? $"max fee per gas less than block base fee: address {address}, maxFeePerGas: 1, baseFee: 2"
            : $"max fee per blob gas less than block blob gas fee: address {address} blobGasFeeCap: 8, blobBaseFee: 9");
    }

    [TestCase(false, "intrinsic gas too low")]
    [TestCase(true, "required balance exceeds 256 bits")]
    public async Task Debug_traceCall_blob_fee_rejection_preserves_earlier_validation(bool balanceOverflow, string expectedError)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        object transaction = new
        {
            from = TestItem.AddressA.ToString(),
            to = TestItem.AddressC.ToString(),
            gas = balanceOverflow ? "0x186a0" : "0x1",
            value = balanceOverflow ? "0x" + new string('f', 64) : "0x0",
            maxFeePerGas = "0x1",
            maxPriorityFeePerGas = "0x0",
            maxFeePerBlobGas = "0x8",
            blobVersionedHashes = new[] { "0x01" + new string('0', 62) }
        };
        object blockOverrides = new { blobBaseFee = "0x9", baseFeePerGas = "0x0" };
        string buffered = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", transaction, "latest",
            new { blockOverrides, streamMode = false });
        string streamed = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", transaction, "latest",
            new { blockOverrides, streamMode = true });
        JObject expected = JObject.Parse(buffered);
        JObject actual = JObject.Parse(streamed);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int?)expected["error"]?["code"], Is.EqualTo(ErrorCodes.InvalidInput), buffered);
            Assert.That((string?)expected["error"]?["message"], Does.Contain(expectedError), buffered);
            Assert.That(actual.ContainsKey("result"), Is.False, streamed);
            Assert.That(JToken.DeepEquals(actual["error"], expected["error"]), Is.True, streamed);
        }
    }

    [Test]
    public async Task Debug_traceCall_blob_fee_rejection_does_not_mask_tracer_errors(
        [Values("unknownTracer", "prestateTracer")] string tracer, [Values(null, "0x0")] string? txIndex)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        ctx.Blockchain.BlockTree.Head!.Header.ExcessBlobGas = 0;
        object options = new
        {
            tracer,
            txIndex,
            tracerConfig = new { diffMode = true, includeEmpty = true },
            blockOverrides = new { blobBaseFee = "0x9" }
        };
        string validFee = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", BlobFeeTransaction(9), "latest", options);
        string invalidFee = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", BlobFeeTransaction(8), "latest", options);
        JToken expected = JToken.Parse(validFee);
        JToken actual = JToken.Parse(invalidFee);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(expected["error"], Is.Not.Null, validFee);
            Assert.That(actual["result"], Is.Null, invalidFee);
            Assert.That(JToken.DeepEquals(actual["error"], expected["error"]), Is.True, invalidFee);
        }
    }

    private static object BlobFeeTransaction(int blobCap, int gasFeeCap = 0) => new
    {
        from = TestItem.AddressA.ToString(),
        to = TestItem.AddressC.ToString(),
        gas = "0x186a0",
        maxFeePerGas = "0x" + gasFeeCap.ToString("x"),
        maxPriorityFeePerGas = "0x0",
        maxFeePerBlobGas = "0x" + blobCap.ToString("x"),
        blobVersionedHashes = new[] { "0x01" + new string('0', 62) }
    };

    private static void AssertBlobFeeRpcError(string response, string detail)
    {
        JToken envelope = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(envelope["result"], Is.Null, response);
            Assert.That(envelope["error"]?["code"]?.Value<int>(), Is.EqualTo(ErrorCodes.InvalidInput), response);
            Assert.That(envelope["error"]?["message"]?.Value<string>(), Is.EqualTo("tracing failed: " + detail), response);
        }
    }

    [Test]
    public async Task Debug_traceCall_preserves_derived_blob_conversion_and_field_precedence(
        [Values(0, 1)] int blobFeeCap, [Values] bool beforeFork, [Values] bool withAccessList)
    {
        const ulong gasCap = 123456;
        IReleaseSpec spec = beforeFork ? Istanbul.Instance : Cancun.Instance;
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { GasCap = gasCap })
            .Build(new TestSpecProvider(spec));
        TraceCallConversionProbe request = new()
        {
            To = TestItem.AddressC,
            MaxFeePerBlobGas = (UInt256)blobFeeCap,
            BlobVersionedHashes = [new byte[32]],
            AccessList = withAccessList ? new AccessListForRpc() : null
        };
        request.BlobVersionedHashes[0][0] = 1;

        string? error = chain.DebugRpcModule.debug_traceCall(request).Result.Error;
        (bool, ulong?, IReleaseSpec?, UInt256?)[] expectedConversions = beforeFork ? []
            : [(true, gasCap, spec, blobFeeCap == 0 ? null : (UInt256)blobFeeCap)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error, Is.EqualTo(beforeFork
                ? $"tracing failed: transaction type not supported: {(withAccessList ? "access list" : "blob")} tx (sender {Address.Zero})"
                : TraceCallConversionProbe.ConversionError));
            Assert.That(request.Conversions, Is.EqualTo(expectedConversions), "trace conversion must retain virtual dispatch, flags and zero-cap omission");
            Assert.That(request.MaxFeePerBlobGas, Is.EqualTo((UInt256)blobFeeCap), "the request must not be mutated");
        }

        Assert.That(request.ToCallTransaction(spec, gasCap).Error, Is.EqualTo(TraceCallConversionProbe.ConversionError));
        Assert.That(request.Conversions[^1], Is.EqualTo((true, (ulong?)gasCap, spec, (UInt256?)blobFeeCap)),
            "sibling calls must not apply trace-only blob defaults");
    }

    private sealed class TraceCallConversionProbe : BlobTransactionForRpc
    {
        public const string ConversionError = "derived blob conversion";
        public List<(bool, ulong?, IReleaseSpec?, UInt256?)> Conversions { get; } = [];

        public override Result<Transaction> ToTransaction(bool validateUserInput = false, ulong? gasCap = null, IReleaseSpec? spec = null)
        {
            Conversions.Add((validateUserInput, gasCap, spec, MaxFeePerBlobGas));
            return ConversionError;
        }
    }

    [TestCase("0x0", false, 0)]
    [TestCase("0x0", true, 0)]
    [TestCase("0x1", false, 1)]
    [TestCase(null, true, 0)]
    public async Task Debug_traceCall_runs_blob_fee_cap_as_geth(string? blobFeeCap, bool withBlobHashes, int expectedBlobBaseFee)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = false })
            .Build(new TestSpecProvider(Cancun.Instance));
        chain.BlockTree.Head!.Header.ExcessBlobGas = 0;
        Dictionary<string, object> call = new()
        {
            ["to"] = TestItem.AddressC.ToString(),
            ["gas"] = "0x186a0"
        };
        if (blobFeeCap is not null) call["maxFeePerBlobGas"] = blobFeeCap;
        if (withBlobHashes) call["blobVersionedHashes"] = new[] { "0x01" + new string('0', 62) };
        // BLOBBASEFEE; MSTORE at 0; RETURN 32 bytes
        object stateOverrides = new Dictionary<string, object> { [TestItem.AddressC.ToString()] = new { code = "0x4a60005260206000f3" } };

        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceCall", call, "latest", new { stateOverrides });
        EthereumJsonSerializer serializer = new();
        TransactionForRpc sibling = serializer.Deserialize<TransactionForRpc>(serializer.Serialize(call))!;
        string? siblingError = !withBlobHashes ? RpcTransactionErrors.BlobFeeCapWithoutBlobHashes
            : blobFeeCap == "0x0" ? RpcTransactionErrors.ZeroMaxFeePerBlobGas : null;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(JToken.Parse(response)["result"]?["failed"]?.Value<bool>(), Is.False, response);
            Assert.That(new UInt256(ParseReturnValue(response), true), Is.EqualTo((UInt256)expectedBlobBaseFee), response);
            Assert.That(sibling.ToCallTransaction(Cancun.Instance).Error, Is.EqualTo(siblingError),
                "trace-only blob fee defaults must not change sibling call conversion");
        }
    }

    [Test]
    public async Task Debug_traceCall_rejects_blob_fee_cap_without_blob_hashes_before_cancun()
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Shanghai.Instance));
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { to = TestItem.AddressC.ToString(), gas = "0x186a0", maxFeePerBlobGas = "0x1" }, "latest");
        Assert.That((string?)JToken.Parse(response)["error"]?["message"], Is.EqualTo($"tracing failed: transaction type not supported: blob tx (sender {Address.Zero})"), response);
    }

    private static void AssertBlobPayment(string response, ulong expectedBalance, ulong expectedFee)
    {
        JToken result = JToken.Parse(response)["result"]!;
        Assert.That(result["failed"]!.Value<bool>(), Is.False, response);
        byte[] output = ParseReturnValue(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new UInt256(output.AsSpan(0, 32), true), Is.EqualTo((UInt256)expectedBalance));
            Assert.That(new UInt256(output.AsSpan(32, 32), true), Is.EqualTo((UInt256)expectedFee));
        }
    }
}
