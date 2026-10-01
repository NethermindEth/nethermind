// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Newtonsoft.Json;
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
            Assert.That(result["callTracer"]!["calls"]?.Count() ?? 0, Is.EqualTo(1));
            Assert.That(result["callTracer"]!["calls"]![0]!["error"]?.Value<string>(), Is.EqualTo(funded ? null : "insufficient balance for transfer"));
        }
    }

    private const string RejectedCreateCallbacks = "{events:[],step:function(){},fault:function(){},enter:function(f){this.events.push({kind:\"enter\",type:f.getType(),from:toHex(f.getFrom()),to:toHex(f.getTo()),gas:f.getGas(),value:f.getValue().toString(),input:toHex(f.getInput())});},exit:function(f){this.events.push({kind:\"exit\",gasUsed:f.getGasUsed(),error:f.getError()||null,output:toHex(f.getOutput())});},result:function(ctx){return {events:this.events,error:ctx.error||null};}}";

    [Test]
    public async Task Debug_traceCall_create_lifecycle_rejected_frames(
        [Values("f0", "f5")] string opcode, [Values("balance", "nonce", "collision")] string reason,
        [Values] bool amsterdam, [Values] bool failParent)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(amsterdam ? (IReleaseSpec)Amsterdam.Instance : Cancun.Instance));
        string code = (opcode == "f5" ? "6000" : "") + "60006000" + (reason == "balance" ? "6001" : "6000") + opcode + (failParent ? "fe" : "00");
        string address = opcode == "f5" ? "0xa7c70de27f8ee63a9ae009c97fe3237578f46fdd" : "0x3817e247023b4f489352758397040b1fd33b300a";
        Dictionary<string, object> overrides = CreateLifecycleOverrides(code);
        overrides[FlatTarget] = new { code = "0x" + code, balance = "0x0", nonce = reason == "nonce" ? "0xffffffffffffffff" : "0x0" };
        if (reason == "collision") overrides[address] = new { code = "0x00", nonce = "0x1" };
        JToken result = await TraceRejectedCreate(ctx, overrides);
        string? parentError = failParent ? "invalid opcode: INVALID" : null;
        Assert.That(result["callTracer"]!["error"]?.Value<string>(), Is.EqualTo(parentError));
        Assert.That(result[RejectedCreateCallbacks]!["error"]!.Value<string>(), Is.EqualTo(parentError));
        if (amsterdam && reason != "collision")
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result["callTracer"]!["calls"], Is.Null);
                Assert.That(result["erc7562Tracer"]!["calls"], Is.Null);
                Assert.That(result[RejectedCreateCallbacks]!["events"]!.Count(), Is.Zero);
            }
            return;
        }
        string error = reason switch { "balance" => "insufficient balance for transfer", "nonce" => "nonce uint64 overflow", _ => "contract address collision" };
        string createType = opcode == "f5" ? "CREATE2" : "CREATE";
        string expectedTo = reason == "nonce" && opcode == "f0" ? "0x7d0390bb588a6c6c4b9c11bef8891e8ac9a33126" : address;
        JToken call = result["callTracer"]!["calls"]![0]!;
        JToken erc = result["erc7562Tracer"]!["calls"]![0]!;
        JToken events = result[RejectedCreateCallbacks]!["events"]!;
        ulong gas = Convert.ToUInt64(call["gas"]!.Value<string>()![2..], 16);
        string gasUsed = reason == "collision" ? call["gas"]!.Value<string>()! : "0x0";
        using (Assert.EnterMultipleScope())
        {
            Assert.That(call["type"]!.Value<string>(), Is.EqualTo(createType));
            Assert.That(call["from"]!.Value<string>(), Is.EqualTo(FlatTarget));
            Assert.That(call["to"], Is.Null);
            Assert.That(call["input"]!.Value<string>(), Is.EqualTo("0x"));
            Assert.That(call["value"]!.Value<string>(), Is.EqualTo(reason == "balance" ? "0x1" : "0x0"));
            Assert.That(call["error"]!.Value<string>(), Is.EqualTo(error));
            Assert.That(call["gasUsed"]!.Value<string>(), Is.EqualTo(gasUsed));
            Assert.That(erc["to"], Is.Null);
            Assert.That(erc["error"]!.Value<string>(), Is.EqualTo(error));
            Assert.That(erc["usedOpcodes"]!.Count(), Is.Zero);
            Assert.That(events.Count(), Is.EqualTo(2));
            Assert.That(events[0]!["type"]!.Value<string>(), Is.EqualTo(createType));
            Assert.That(events[0]!["to"]!.Value<string>(), Is.EqualTo(expectedTo));
            Assert.That(events[0]!["gas"]!.Value<ulong>(), Is.EqualTo(gas));
            Assert.That(events[1]!["gasUsed"]!.Value<ulong>(), Is.EqualTo(reason == "collision" ? gas : 0));
            Assert.That(events[1]!["error"]!.Value<string>(), Is.EqualTo(error));
            if (!amsterdam)
            {
                Assert.That(gas, Is.EqualTo(opcode == "f5" ? 46254UL : 46257UL));
                if (!failParent)
                    Assert.That(result["callTracer"]!["gasUsed"]!.Value<string>(), Is.EqualTo(reason == "collision" ? "0x183c2" : opcode == "f5" ? "0xcf14" : "0xcf11"));
            }
        }
    }

    [Test]
    public async Task Debug_traceCall_create_lifecycle_depth_rejection()
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Homestead.Instance));
        string call = "6000600060006000600073" + FlatTarget[2..] + "60645a03f1";
        string code = call + "1560" + (call.Length / 2 + 5).ToString("x2") + "57005b600060006000f000";
        JToken result = await TraceRejectedCreate(ctx, CreateLifecycleOverrides(code), "0xf4240", timeout: "1m");
        JToken frame = result["callTracer"]!;
        int depth = 1;
        while (frame["calls"] is JArray { Count: 1 } children)
        {
            frame = children[0];
            depth++;
        }
        Assert.That(depth, Is.EqualTo(1025));
        JToken create = frame["calls"]![1]!;
        JArray events = (JArray)result[RejectedCreateCallbacks]!["events"]!;
        JToken enter = events.Single(e => e["type"]?.Value<string>() == "CREATE");
        JToken exit = events[events.IndexOf(enter) + 1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(create["type"]!.Value<string>(), Is.EqualTo("CREATE"));
            Assert.That(create["error"]!.Value<string>(), Is.EqualTo("max call depth exceeded"));
            Assert.That(create["to"], Is.Null);
            Assert.That(create["gasUsed"]!.Value<string>(), Is.EqualTo("0x0"));
            Assert.That(exit["error"]!.Value<string>(), Is.EqualTo("max call depth exceeded"));
            Assert.That(exit["gasUsed"]!.Value<ulong>(), Is.Zero);
            Assert.That(result["callTracer"]!["error"], Is.Null);
            Assert.That(result[RejectedCreateCallbacks]!["error"]!.Type, Is.EqualTo(JTokenType.Null));
        }
    }

    private static async Task<JToken> TraceRejectedCreate(Context ctx, Dictionary<string, object> overrides, string gas = "0x186a0", string? timeout = null)
    {
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", new { from = FlatSender, to = FlatTarget, gas }, "latest", new
        {
            tracer = "muxTracer",
            tracerConfig = new Dictionary<string, object> { ["callTracer"] = new { }, ["erc7562Tracer"] = new { }, [RejectedCreateCallbacks] = new { } },
            stateOverrides = overrides,
            timeout
        });
        using StringReader text = new(response);
        using JsonTextReader reader = new(text) { MaxDepth = 4096 };
        JToken envelope = JToken.ReadFrom(reader);
        Assert.That(envelope["error"], Is.Null, response);
        return envelope["result"]!;
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
