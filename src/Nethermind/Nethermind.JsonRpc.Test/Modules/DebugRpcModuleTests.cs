// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.DebugModule;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

[Parallelizable(ParallelScope.Self)]
public partial class DebugRpcModuleTests
{
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
            "tracing failed: insufficient funds for gas * price + value: address ",
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
            (object)new { from = $"{freshC}", to = $"{TestItem.AddressC}", maxFeePerGas = "0x1", maxPriorityFeePerGas = "0x1" },
            "tracing failed: insufficient funds for gas * price + value: address ",
            ErrorCodes.InvalidInput)
        { TestName = "InsufficientFundsForGasPriceValue" };
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
}
