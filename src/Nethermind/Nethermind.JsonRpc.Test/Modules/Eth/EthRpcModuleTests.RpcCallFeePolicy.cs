// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Messages;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    private const string FeePolicySender = "0xc010000000000000000000000000000000000000";
    private const string FeePolicyTarget = "0xc020000000000000000000000000000000000000";
    private const string FeePolicyChild = "0xc030000000000000000000000000000000000000";
    // GASPRICE, BASEFEE, CALLER BALANCE, ORIGIN BALANCE, SELFBALANCE, CALLVALUE.
    private const string FeePolicySnapshot = "0x3a6000524860205233316040523231606052476080523460a05260c06000f3";
    private const string FeePolicyLegacy = "\"gasPrice\":\"0x14\"";
    private const string FeePolicy1559 = "\"maxFeePerGas\":\"0x14\",\"maxPriorityFeePerGas\":\"0x3\"";

    private static Task<Context> FeePolicyContext(bool? chargeFees = false, bool validateLegacy = false,
        FeePolicyExecutions? executions = null) => Context.Create(new TestSpecProvider(London.Instance), configurer: builder =>
    {
        if (chargeFees.HasValue) builder.AddSingleton(new RpcCallExecutionPolicy(chargeFees.Value, validateLegacy));
        if (executions is not null)
            builder.AddDecorator<ITransactionProcessor>((_, inner) => new FeePolicyRecordingProcessor(inner, executions));
    });

    private static JsonElement FeePolicyRequest(string fees = FeePolicyLegacy, string gas = "0x186a0", string value = "0x0") =>
        JsonSerializer.Deserialize<JsonElement>($$"""{"from":"{{FeePolicySender}}","to":"{{FeePolicyTarget}}","gas":"{{gas}}","value":"{{value}}"{{(fees.Length == 0 ? "" : "," + fees)}}} """);

    private static Dictionary<string, object> FeePolicyState(string balance = "0x0", string code = FeePolicySnapshot) => new()
    {
        [FeePolicySender] = new { balance, nonce = "0x0", code = "0x" },
        [FeePolicyTarget] = new { balance = "0x7", code },
    };

    private static async Task<JToken> FeePolicyCall(Context ctx, JsonElement request,
        Dictionary<string, object>? state = null, string baseFee = "0x1", string method = "eth_call") =>
        JToken.Parse(await ctx.Test.TestEthRpc(method, request, "latest", state ?? FeePolicyState(),
            new { baseFeePerGas = baseFee }));

    private static string FeePolicyWords(params ulong[] words) => "0x" + string.Concat(words.Select(word => word.ToString("x64")));

    private static void AssertFeePolicyOutput(JToken response, string expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response["error"], Is.Null, response.ToString());
            Assert.That((string?)response["result"], Is.EqualTo(expected), response.ToString());
        }
    }

    [Test]
    public async Task RpcCallFeePolicy_unfunded_explicit_gas_requires_opt_in(
        [Values(null, true, false)] bool? chargeFees, [Values] bool dynamicFees)
    {
        using Context ctx = await FeePolicyContext(chargeFees, validateLegacy: true);
        JToken response = await FeePolicyCall(ctx, FeePolicyRequest(dynamicFees ? FeePolicy1559 : FeePolicyLegacy));
        if (chargeFees is false)
            AssertFeePolicyOutput(response, FeePolicyWords(dynamicFees ? 4UL : 20UL, 1, 0, 0, 7, 0));
        else
            Assert.That((string?)response["error"]?["message"], Does.Contain(TxErrorMessages.InsufficientFundsForGas), response.ToString());
    }

    [Test]
    public async Task RpcCallFeePolicy_balance_opcodes_observe_only_value_or_upfront_fee_debit(
        [Values(null, true, false)] bool? chargeFees, [Values] bool dynamicFees)
    {
        using Context ctx = await FeePolicyContext(chargeFees, validateLegacy: true);
        ulong price = dynamicFees ? 4UL : 20UL;
        ulong balance = 10_000_000;
        ulong remaining = balance - 5 - (chargeFees is false ? 0 : 100_000 * price);
        JToken response = await FeePolicyCall(ctx, FeePolicyRequest(dynamicFees ? FeePolicy1559 : FeePolicyLegacy, value: "0x5"),
            FeePolicyState("0x989680"));
        AssertFeePolicyOutput(response, FeePolicyWords(price, 1, remaining, remaining, 12, 5));
    }

    [TestCase("", 0UL)]
    [TestCase("\"gasPrice\":\"0x0\"", 0UL)]
    [TestCase("\"maxFeePerGas\":\"0x0\",\"maxPriorityFeePerGas\":\"0x0\"", 0UL)]
    public async Task RpcCallFeePolicy_zero_fees_preserve_zero_basefee_context(string fees, ulong price)
    {
        using Context ctx = await FeePolicyContext();
        AssertFeePolicyOutput(await FeePolicyCall(ctx, FeePolicyRequest(fees), baseFee: "0x64"),
            FeePolicyWords(price, 0, 0, 0, 7, 0));
    }

    [TestCase("0x0", true)]
    [TestCase("0x5", true)]
    [TestCase("0x6", false)]
    public async Task RpcCallFeePolicy_value_affordability_is_not_disabled(string value, bool succeeds)
    {
        using Context ctx = await FeePolicyContext();
        JToken response = await FeePolicyCall(ctx, FeePolicyRequest(value: value), FeePolicyState("0x5"));
        if (succeeds)
        {
            ulong transferred = Convert.ToUInt64(value[2..], 16);
            AssertFeePolicyOutput(response, FeePolicyWords(20, 1, 5 - transferred, 5 - transferred, 7 + transferred, transferred));
        }
        else
            Assert.That(response["error"], Is.Not.Null, response.ToString());
    }

    [Test]
    public async Task RpcCallFeePolicy_fields_are_independent([Values] bool chargeFees, [Values] bool validateLegacy)
    {
        using Context ctx = await FeePolicyContext(chargeFees, validateLegacy);
        JToken response = await FeePolicyCall(ctx, FeePolicyRequest("\"gasPrice\":\"0x2\""),
            FeePolicyState("0x989680"), "0x64");
        if (validateLegacy)
            Assert.That((string?)response["error"]?["message"], Does.Contain("max fee per gas less than block base fee"), response.ToString());
        else
        {
            ulong remaining = 10_000_000UL - (chargeFees ? 200_000UL : 0);
            AssertFeePolicyOutput(response, FeePolicyWords(2, 100, remaining, remaining, 7, 0));
        }
    }

    [Test]
    public async Task RpcCallFeePolicy_fee_cap_validation_precedes_intrinsic_gas_and_invalid_opcode(
        [Values("0x0", "0x186a0")] string gas, [Values] bool priorityAboveCap,
        [Values] bool validateLegacy)
    {
        using Context ctx = await FeePolicyContext(false, validateLegacy);
        string fees = "\"maxFeePerGas\":\"0x2\",\"maxPriorityFeePerGas\":\"" + (priorityAboveCap ? "0x3" : "0x1") + "\"";
        JToken response = await FeePolicyCall(ctx, FeePolicyRequest(fees, gas), FeePolicyState(code: "0xfe"), "0x64");
        string sender = new Address(FeePolicySender).ToString(withEip55Checksum: true);
        // The policy's pre-check gives the processor's own error, as a node without a policy reports it.
        string expected = priorityAboveCap
            ? $"{TxErrorMessages.TipAboveFeeCap}: address {sender}, maxPriorityFeePerGas: 3, maxFeePerGas: 2"
            : $"max fee per gas less than block base fee: address {sender}, maxFeePerGas: 2, baseFee: 100";
        using (Assert.EnterMultipleScope())
        {
            Assert.That((string?)response["error"]?["message"], Does.Contain(expected), response.ToString());
            Assert.That((int?)response["error"]?["code"], Is.EqualTo(-32000), response.ToString());
        }
    }

    [TestCase(32)]
    [TestCase(64)]
    public async Task RpcCallFeePolicy_huge_legacy_price_does_not_multiply_fees_or_require_funding(int hexDigits)
    {
        using Context ctx = await FeePolicyContext();
        string quantity = new('f', hexDigits);
        JToken response = await FeePolicyCall(ctx, FeePolicyRequest($"\"gasPrice\":\"0x{quantity}\""));
        AssertFeePolicyOutput(response, "0x" + quantity.PadLeft(64, '0') + FeePolicyWords(1, 0, 0, 7, 0)[2..]);
    }

    [Test]
    public async Task RpcCallFeePolicy_nested_call_preserves_caller_origin_and_value_balances()
    {
        using Context ctx = await FeePolicyContext();
        // CALL child with value 3, copying its six-word snapshot into memory; return it plus the CALL success bit.
        string parentCode = "0x60c0600060006000600373" + FeePolicyChild[2..] + "5af160c05260e06000f3";
        Dictionary<string, object> state = FeePolicyState("0x5", parentCode);
        state[FeePolicyChild] = new { balance = "0xb", code = FeePolicySnapshot };
        JToken response = await FeePolicyCall(ctx, FeePolicyRequest(value: "0x5"), state);
        AssertFeePolicyOutput(response, FeePolicyWords(20, 1, 9, 0, 14, 3, 1));
    }

    [Test]
    public async Task RpcCallFeePolicy_intrinsic_gas_is_still_required([Values] bool dynamicFees)
    {
        using Context ctx = await FeePolicyContext();
        JToken response = await FeePolicyCall(ctx, FeePolicyRequest(dynamicFees ? FeePolicy1559 : FeePolicyLegacy, gas: "0x0"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response["error"], Is.Not.Null, response.ToString());
            Assert.That((string?)response["error"]?["message"], Does.Contain("intrinsic gas"), response.ToString());
        }
    }

    [Test]
    public async Task RpcCallFeePolicy_failure_text_rerun_keeps_options_and_restores_state()
    {
        FeePolicyExecutions executions = new();
        using Context ctx = await FeePolicyContext(executions: executions);
        _ = ctx.Test;
        executions.Options.Clear();
        // SSTORE then INVALID forces the diagnostic rerun, not the REVERT shortcut.
        JToken response = await FeePolicyCall(ctx, FeePolicyRequest(), FeePolicyState(code: "0x6001600055fe"));
        Assert.That((string?)response["error"]?["message"], Does.Contain("invalid opcode"), response.ToString());
        ExecutionOptions[] options = executions.Options.ToArray();
        Assert.That(options.Length, Is.GreaterThanOrEqualTo(2), "the real processor must execute the diagnostic rerun");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(options, Is.All.EqualTo(options[0]));
            Assert.That(options[0].HasFlag(ExecutionOptions.SkipTransactionFees), Is.True);
            Assert.That(options[0].HasFlag(ExecutionOptions.SkipLegacyGasPriceValidation), Is.True);
        }
        AssertFeePolicyOutput(await FeePolicyCall(ctx, FeePolicyRequest(), FeePolicyState(code: "0x60005460005260206000f3")), FeePolicyWords(0));
    }

    [Test]
    public async Task RpcCallFeePolicy_successive_calls_restore_overrides_balances_and_storage()
    {
        using Context ctx = await FeePolicyContext();
        string balanceBefore = await ctx.Test.TestEthRpc("eth_getBalance", FeePolicySender, "latest");
        string targetBefore = await ctx.Test.TestEthRpc("eth_getBalance", FeePolicyTarget, "latest");
        for (int i = 0; i < 2; i++)
        {
            AssertFeePolicyOutput(await FeePolicyCall(ctx, FeePolicyRequest(value: "0x5"), FeePolicyState("0x5")),
                FeePolicyWords(20, 1, 0, 0, 12, 5));
            AssertFeePolicyOutput(await FeePolicyCall(ctx, FeePolicyRequest(FeePolicy1559)), FeePolicyWords(4, 1, 0, 0, 7, 0));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ctx.Test.TestEthRpc("eth_getBalance", FeePolicySender, "latest"), Is.EqualTo(balanceBefore));
            Assert.That(await ctx.Test.TestEthRpc("eth_getBalance", FeePolicyTarget, "latest"), Is.EqualTo(targetBefore));
        }
        JToken unoverridden = JToken.Parse(await ctx.Test.TestEthRpc("eth_call", FeePolicyRequest(""), "latest"));
        AssertFeePolicyOutput(unoverridden, "0x");
    }

    [Test]
    public async Task RpcCallFeePolicy_estimate_keeps_affordability_after_fee_free_call([Values] bool dynamicFees)
    {
        using Context ctx = await FeePolicyContext();
        JsonElement request = FeePolicyRequest(dynamicFees ? FeePolicy1559 : FeePolicyLegacy);
        AssertFeePolicyOutput(await FeePolicyCall(ctx, request), FeePolicyWords(dynamicFees ? 4UL : 20UL, 1, 0, 0, 7, 0));
        JToken estimate = await FeePolicyCall(ctx, request, method: "eth_estimateGas");
        Assert.That((string?)estimate["error"]?["message"], Does.Contain("insufficient funds"), estimate.ToString());
        AssertFeePolicyOutput(await FeePolicyCall(ctx, request), FeePolicyWords(dynamicFees ? 4UL : 20UL, 1, 0, 0, 7, 0));
    }

    [Test]
    public async Task RpcCallFeePolicy_access_list_follows_call_fee_semantics(
        [Values(null, true, false)] bool? chargeFees, [Values] bool dynamicFees)
    {
        using Context ctx = await FeePolicyContext(chargeFees, validateLegacy: true);
        JToken response = JToken.Parse(await ctx.Test.TestEthRpc("eth_createAccessList",
            FeePolicyRequest(dynamicFees ? FeePolicy1559 : FeePolicyLegacy), "latest", FeePolicyState()));
        if (chargeFees is false)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(response["error"], Is.Null, response.ToString());
                Assert.That(response["result"]?["error"], Is.Null, response.ToString());
                Assert.That((string?)response["result"]?["gasUsed"], Is.Not.EqualTo("0x0"), response.ToString());
            }
        }
        else
            Assert.That((string?)response["error"]?["message"], Does.Contain(TxErrorMessages.InsufficientFundsForGas), response.ToString());
    }

    [Test]
    public async Task RpcCallFeePolicy_debug_retains_upfront_payment([Values] bool registerPolicy)
    {
        using Context ctx = await FeePolicyContext(registerPolicy ? false : null);
        JsonElement request = FeePolicyRequest();
        Dictionary<string, object> state = FeePolicyState("0x989680");
        if (registerPolicy)
            AssertFeePolicyOutput(await FeePolicyCall(ctx, request, state), FeePolicyWords(20, 1, 10_000_000, 10_000_000, 7, 0));
        string response = await RpcTest.TestSerializedRequest(ctx.Test.DebugRpcModule, "debug_traceCall", request, "latest",
            new { tracer = "callTracer", stateOverrides = state, blockOverrides = new { baseFeePerGas = "0x1" } });
        JToken json = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(json["error"], Is.Null, response);
            Assert.That((string?)json["result"]?["output"], Is.EqualTo(FeePolicyWords(20, 1, 8_000_000, 8_000_000, 7, 0)), response);
        }
    }

    [Test]
    public async Task RpcCallFeePolicy_simulate_retains_fee_debit_and_settlement([Values] bool validation)
    {
        using Context ctx = await FeePolicyContext();
        // STOP costs exactly intrinsic gas, making the second call's observed balance deterministic.
        var payload = new
        {
            validation,
            blockStateCalls = new[]
            {
                new
                {
                    blockOverrides = new { baseFeePerGas = "0x1" },
                    stateOverrides = FeePolicyState("0x989680", "0x00"),
                    calls = new[]
                    {
                        FeePolicyRequest(),
                        FeePolicyRequest("\"gasPrice\":\"0x14\"", value: "0x0")
                    }
                }
            }
        };
        // Observe sender balance through a separate target so the first call has no VM gas cost.
        Dictionary<string, object> state = payload.blockStateCalls[0].stateOverrides;
        state[FeePolicyChild] = new { code = FeePolicySnapshot, balance = "0x7" };
        JsonElement second = JsonSerializer.Deserialize<JsonElement>(FeePolicyRequest().GetRawText().Replace(FeePolicyTarget, FeePolicyChild));
        payload.blockStateCalls[0].calls[1] = second;
        JToken response = JToken.Parse(await ctx.Test.TestEthRpc("eth_simulateV1", payload, "latest"));
        Assert.That(response["error"], Is.Null, response.ToString());
        JToken calls = response["result"]![0]!["calls"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That((string?)calls[0]?["status"], Is.EqualTo("0x1"), response.ToString());
            Assert.That((string?)calls[1]?["status"], Is.EqualTo("0x1"), response.ToString());
            Assert.That((string?)calls[1]?["returnData"], Is.EqualTo(FeePolicyWords(20, 1, 7_580_000, 7_580_000, 7, 0)), response.ToString());
        }
    }

    private sealed class FeePolicyExecutions
    {
        public List<ExecutionOptions> Options { get; } = [];
    }

    private sealed class FeePolicyRecordingProcessor(ITransactionProcessor inner, FeePolicyExecutions executions) : ITransactionProcessor
    {
        public TransactionResult Process(Transaction transaction, ITxTracer txTracer, ExecutionOptions options)
        {
            executions.Options.Add(options);
            return inner.Process(transaction, txTracer, options);
        }

        public void SetBlockExecutionContext(BlockHeader blockHeader) => inner.SetBlockExecutionContext(blockHeader);
        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) => inner.SetBlockExecutionContext(in blockExecutionContext);
    }
}
