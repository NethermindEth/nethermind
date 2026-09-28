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
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    [TestCase(0, 0, null)]
    [TestCase(0, 0, "0x0")]
    [TestCase(9, 0, null)]
    [TestCase(9, 0, "0x0")]
    [TestCase(9, 9, null)]
    [TestCase(9, 9, "0x0")]
    [TestCase(9, 8, null)]
    [TestCase(9, 8, "0x0")]
    public async Task Debug_traceCall_blob_fee_override_applies_to_upfront_payment(int fee, int cap, string? txIndex)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = false })
            .Build(new TestSpecProvider(Cancun.Instance));
        IDebugRpcModule module = chain.DebugRpcModule;
        chain.BlockTree.Head!.Header.ExcessBlobGas = 0;
        const ulong balance = 0x100000000;
        object stateOverrides = new Dictionary<string, object>
        {
            [TestItem.AddressA.ToString()] = new { balance = "0x100000000" },
            [TestItem.AddressC.ToString()] = new { code = "0x32316000524a60205260406000f3" }
        };
        static object Transaction(int blobCap) => new
        {
            from = TestItem.AddressA.ToString(),
            to = TestItem.AddressC.ToString(),
            gas = "0x186a0",
            maxFeePerGas = "0x0",
            maxPriorityFeePerGas = "0x0",
            maxFeePerBlobGas = "0x" + blobCap.ToString("x"),
            blobVersionedHashes = new[] { "0x01" + new string('0', 62) }
        };
        object canonicalOptions = new { stateOverrides, txIndex };
        string before = await RpcTest.TestSerializedRequest(module, "debug_traceCall", Transaction(9), "latest", canonicalOptions);
        AssertBlobPayment(before, balance - 131072, 1);

        string response = await RpcTest.TestSerializedRequest(module, "debug_traceCall", Transaction(cap), "latest",
            new { stateOverrides, txIndex, blockOverrides = new { blobBaseFee = "0x" + fee.ToString("x") } });
        int effectiveFee = cap == 0 ? 0 : fee;
        if (cap < effectiveFee)
        {
            JToken error = JToken.Parse(response)["error"]!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(error["code"]!.Value<int>(), Is.EqualTo(ErrorCodes.InvalidInput));
                Assert.That(error["message"]!.Value<string>(), Does.Contain("max fee per blob gas less than block blob gas fee"));
                Assert.That(error["message"]!.Value<string>(), Does.Contain("blobBaseFee: 9"));
            }
        }
        else
        {
            AssertBlobPayment(response, balance - (ulong)effectiveFee * 131072, (ulong)effectiveFee);
        }

        string after = await RpcTest.TestSerializedRequest(module, "debug_traceCall", Transaction(9), "latest", canonicalOptions);
        AssertBlobPayment(after, balance - 131072, 1);
        string zeroCap = await RpcTest.TestSerializedRequest(module, "debug_traceCall", Transaction(0), "latest", canonicalOptions);
        AssertBlobPayment(zeroCap, balance, 0);
    }

    [TestCase(0)]
    [TestCase(9)]
    public async Task Debug_traceCall_blob_fee_override_does_not_affect_indexed_prefix(int fee)
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
