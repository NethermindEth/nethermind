// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    private const string CreateLifecycleTracer = "{events:[],capture:function(kind,log){var op=log.op.toString();if(op!==\"CREATE\"&&op!==\"CREATE2\"&&op!==\"SELFDESTRUCT\")return;this.events.push({kind:kind,op:op,cost:log.getCost(),error:log.getError()||null,memory:log.memory.length(),stack:log.stack.length()});},step:function(log){this.capture(\"step\",log);},fault:function(log){this.capture(\"fault\",log);},result:function(ctx){return {events:this.events,error:ctx.error||null};}}";

    private static IEnumerable<TestCaseData> CreateLifecycleGasCases()
    {
        yield return new("602160006000f000", 100000, 32010, null);
        yield return new("602160006000f000", 53010, 32010, "out of gas");
        yield return new("602160006000f000", 53008, 32000, "out of gas");
        yield return new("6000602160006000f500", 100000, 32022, null);
        yield return new("6000602160006000f500", 53013, 32022, "out of gas");
        yield return new("6000602160006000f500", 53011, 32000, "out of gas");
        yield return new("6000ff", 100000, 7600, null);
        yield return new("6000ff", 26004, 5000, "out of gas: out of gas");
        yield return new("6000ff", 26002, 5000, "out of gas");
        yield return new("61c00160006000f000", 100000, 32000, "out of gas: max initcode size exceeded: code size 49153 limit 49152");
        yield return new("600061c00160006000f500", 100000, 32000, "out of gas: max initcode size exceeded: code size 49153 limit 49152");
        yield return new("61c00060006000f000", 100000, 44288, null);
        yield return new("600061c00060006000f500", 100000, 53504, null);
        foreach (string opcode in new[] { "f0", "f5" })
        {
            string salt = opcode == "f5" ? "6000" : "";
            string maximumOffset = "7f" + new string('f', 64);
            yield return new(salt + "6001" + maximumOffset + "6000" + opcode, 100000, 32000, "gas uint64 overflow");
            yield return new(salt + "60016420000000006000" + opcode, 100000, 32000, "out of gas: gas uint64 overflow");
            const long largeWords = 0x4000001;
            long largeMemoryCost = largeWords * 3 + largeWords * largeWords / 512;
            yield return new(salt + "600163800000006000" + opcode, 100000, 32000 + largeMemoryCost + (opcode == "f5" ? 8 : 2), "out of gas");
            yield return new(salt + "6000" + maximumOffset + "6000" + opcode + "00", 100000, 32000, null);
        }
    }

    [TestCaseSource(nameof(CreateLifecycleGasCases))]
    public async Task Debug_traceCall_js_create_lifecycle_dynamic_cost(string code, int gas, long expectedCost, string? expectedError)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        Dictionary<string, object> overrides = CreateLifecycleOverrides(code);
        JToken result = await TraceCreateLifecycle(ctx, overrides, gas);
        AssertCreateLifecycleEvent(result, expectedCost, expectedError, code.Contains("f5") ? 4 : code.EndsWith("ff") ? 1 : 3);
        Assert.That(result["error"]!.Value<string>(), Is.EqualTo(expectedError));
    }

    [TestCase("602160006000f000", 32000, 3)]
    [TestCase("6000602160006000f500", 32000, 4)]
    [TestCase("6000ff", 5000, 1)]
    [TestCase("6000ff", 0, 1, "out of gas: write protection", true)]
    [TestCase("60017fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff6000f0", 32000, 3, "gas uint64 overflow")]
    [TestCase("60016420000000006000f0", 32000, 3)]
    [TestCase("61c00160006000f0", 32000, 3)]
    public async Task Debug_traceCall_js_create_lifecycle_static_cost(string childCode, long expectedCost, int stackSize, string expectedError = "out of gas: write protection", bool preBerlin = false)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(preBerlin ? (IReleaseSpec)Istanbul.Instance : Cancun.Instance));
        Dictionary<string, object> overrides = CreateLifecycleOverrides(FlatCall(FlatChild, "fa") + "00");
        overrides[FlatChild] = new { code = "0x" + childCode, balance = "0x0" };
        JToken result = await TraceCreateLifecycle(ctx, overrides, 200000);
        AssertCreateLifecycleEvent(result, expectedCost, expectedError, stackSize);
        Assert.That(result["error"]!.Type, Is.EqualTo(JTokenType.Null), "A handled child failure must not set the parent's error.");
    }

    [Test]
    public async Task Debug_traceCall_js_create_lifecycle_rejected_value_keeps_execution([Values("f0", "f5")] string opcode, [Values] bool funded)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        string code = (opcode == "f5" ? "6000" : "") + "600060006001" + opcode + "00";
        Dictionary<string, object> overrides = CreateLifecycleOverrides(code);
        overrides[FlatTarget] = new { code = "0x" + code, balance = funded ? "0x1" : "0x0" };
        const string tracer = "{steps:[],step:function(log){if(log.getDepth()===1&&log.op.toString()===\"STOP\")this.steps.push(log.stack.peek(0).toString(16));},fault:function(){},result:function(ctx){return {steps:this.steps,error:ctx.error||null};}}";
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", FlatTransaction(), "latest", new
        {
            tracer = "muxTracer",
            tracerConfig = new Dictionary<string, object> { [tracer] = new { }, [CreateLifecycleTracer] = new { }, ["callTracer"] = new { } },
            stateOverrides = overrides
        });
        JToken result = JToken.Parse(response)["result"]!;
        AssertCreateLifecycleEvent(result[CreateLifecycleTracer]!, 32000, null, opcode == "f5" ? 4 : 3);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result[tracer]!["steps"]!.Count(), Is.EqualTo(1));
            Assert.That(result[tracer]!["steps"]![0]!.Value<string>() == "0", Is.EqualTo(!funded));
            Assert.That(result[tracer]!["error"]!.Type, Is.EqualTo(JTokenType.Null));
            Assert.That(result["callTracer"]!["calls"]?.Count() ?? 0, Is.EqualTo(funded ? 1 : 0));
        }
    }

    [TestCase("6000ff", "0x1", 32600)]
    [TestCase("6000ff", "0x1", 32600, 53003, "out of gas")]
    [TestCase("611000ff", "0x1", 5000)]
    [TestCase("6001ff", "0x0", 5000)]
    public async Task Debug_traceCall_js_selfdestruct_beneficiary_cost(string code, string balance, long expectedCost, int gas = 100000, string? expectedError = null)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        Dictionary<string, object> overrides = CreateLifecycleOverrides(code);
        overrides[FlatTarget] = new { code = "0x" + code, balance };
        JToken result = await TraceCreateLifecycle(ctx, overrides, gas);
        AssertCreateLifecycleEvent(result, expectedCost, expectedError, 1);
        Assert.That(result["error"]!.Value<string>(), Is.EqualTo(expectedError));
    }

    private static Dictionary<string, object> CreateLifecycleOverrides(string code) => new()
    {
        [FlatSender] = new { balance = "0x100000000" },
        [FlatTarget] = new { code = "0x" + code, balance = "0x0" },
        ["0x0000000000000000000000000000000000000000"] = new { balance = "0x0", nonce = "0x0", code = "0x" }
    };

    private static async Task<JToken> TraceCreateLifecycle(Context ctx, Dictionary<string, object> overrides, int gas)
    {
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { from = FlatSender, to = FlatTarget, gas = "0x" + gas.ToString("x") }, "latest",
            new { tracer = CreateLifecycleTracer, stateOverrides = overrides, blockOverrides = new { feeRecipient = FlatChild } });
        JToken envelope = JToken.Parse(response);
        Assert.That(envelope["error"], Is.Null, response);
        return envelope["result"]!;
    }

    private static void AssertCreateLifecycleEvent(JToken result, long cost, string? error, int stackSize)
    {
        JToken events = result["events"]!;
        Assert.That(events.Count(), Is.EqualTo(1), result.ToString());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(events[0]!["kind"]!.Value<string>(), Is.EqualTo("step"));
            Assert.That(events[0]!["cost"]!.Value<long>(), Is.EqualTo(cost));
            Assert.That(events[0]!["error"]!.Value<string>(), Is.EqualTo(error));
            Assert.That(events[0]!["memory"]!.Value<int>(), Is.Zero);
            Assert.That(events[0]!["stack"]!.Value<int>(), Is.EqualTo(stackSize));
        }
    }
}
