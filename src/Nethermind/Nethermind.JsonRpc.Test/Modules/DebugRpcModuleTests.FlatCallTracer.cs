// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    private const string FlatSender = "0x0000000000000000000000000000000000001000";
    private const string FlatTarget = "0x0000000000000000000000000000000000001001";
    private const string FlatChild = "0x0000000000000000000000000000000000001002";
    private const string FlatPrecompile = "0x0000000000000000000000000000000000000004";

    [Test]
    public async Task Debug_traceCall_flatCallTracer_empty_context_and_schema(
        [Values(null, "0x0")] string? txIndex, [Values] bool overrideBlock, [Values] bool mux)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        object config = new { onlyTopCall = true, withLog = true };
        object options = mux
            ? new { tracer = "muxTracer", tracerConfig = (object)new Dictionary<string, object> { ["flatCallTracer"] = config, ["callTracer"] = new { } }, txIndex, stateOverrides = FlatOverrides("00"), blockOverrides = overrideBlock ? new { number = "0x123456", time = "0x123456" } : null }
            : new { tracer = "flatCallTracer", tracerConfig = config, txIndex, stateOverrides = FlatOverrides("00"), blockOverrides = overrideBlock ? new { number = "0x123456", time = "0x123456" } : null };
        JToken response = JToken.Parse(await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", FlatTransaction(), "latest", options));
        JToken flat = mux ? response["result"]!["flatCallTracer"]! : response["result"]!;
        JToken expected = JToken.Parse($$"""
            [{"action":{"callType":"call","from":"{{FlatSender}}","gas":"0x186a0","input":"0x","to":"{{FlatTarget}}","value":"0x0"},
            "blockHash":null,"blockNumber":0,"result":{"gasUsed":"0x5208","output":"0x"},"subtraces":0,"traceAddress":[],
            "transactionHash":null,"transactionPosition":0,"type":"call"}]
            """);
        Assert.That(JToken.DeepEquals(flat, expected), Is.True, response.ToString());
        if (mux)
            Assert.That(response["result"]!["callTracer"]!["type"]!.Value<string>(), Is.EqualTo("CALL"));
    }

    [TestCase("60006000fd", false, "execution reverted", "0x")]
    [TestCase("60006000fd", true, "Reverted", "0x")]
    [TestCase("602a60005360016000fd", true, "Reverted", "0x2a")]
    [TestCase("fe", false, "invalid opcode: INVALID", null)]
    [TestCase("fe", true, "Bad instruction", null)]
    [TestCase("0c", false, "invalid opcode: opcode 0xc not defined", null)]
    [TestCase("0c", true, "Bad instruction", null)]
    [TestCase("e0", false, "invalid opcode: RJUMP", null)]
    [TestCase("01", true, "Stack underflow", null)]
    [TestCase("600056", true, "Bad jump destination", null)]
    [TestCase("6001600060003e", true, "Out of bounds", null)]
    [TestCase("5b600056", true, "Out of gas", null)]
    public async Task Debug_traceCall_flatCallTracer_error_results(string code, bool convert, string error, string? output)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        JToken rows = await TraceFlatCall(ctx, code, new { convertParityErrors = convert });
        JToken frame = rows[0]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(frame["error"]!.Value<string>(), Is.EqualTo(error));
            Assert.That(frame["result"]?["output"]?.Value<string>(), Is.EqualTo(output));
            if (output is null) Assert.That(frame["result"], Is.Null);
            else Assert.That(frame["result"]!["gasUsed"]!.Type, Is.EqualTo(JTokenType.String));
        }
    }

    private static readonly (string Code, string Name, bool London)[] FlatInvalidOpcodes =
    [
        ("fe", "INVALID", false),
        ("0c", "opcode 0xc not defined", false),
        ("e0", "RJUMP", false),
        ("5f", "PUSH0", true),
        ("5c", "TLOAD", true)
    ];

    [Test]
    public async Task Debug_traceCall_flatCallTracer_preserves_faulting_opcode(
        [ValueSource(nameof(FlatInvalidOpcodes))] (string Code, string Name, bool London) opcode,
        [Values] bool nested, [Values("callTracer", "flatCallTracer")] string tracer)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(opcode.London ? (IReleaseSpec)London.Instance : Cancun.Instance));
        string failingCode = "600050" + opcode.Code;
        Dictionary<string, object> overrides = FlatOverrides(nested ? FlatCall(FlatChild, "f1") + "00" : failingCode);
        if (nested) overrides[FlatChild] = new { code = "0x" + failingCode };
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", FlatTransaction(), "latest",
            new { tracer, stateOverrides = overrides });
        JToken result = JToken.Parse(response)["result"]!;
        JToken frame = tracer == "flatCallTracer" ? result[nested ? 1 : 0]! : nested ? result["calls"]![0]! : result;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(frame["error"]!.Value<string>(), Is.EqualTo("invalid opcode: " + opcode.Name), response);
            if (nested)
            {
                JToken parent = tracer == "flatCallTracer" ? result[0]! : result;
                Assert.That(parent["error"], Is.Null, response);
            }
            if (tracer == "flatCallTracer") Assert.That(frame["result"], Is.Null, response);
        }
    }

    [TestCase("fe", "invalid opcode: INVALID")]
    [TestCase("0c", "invalid opcode: opcode 0xc not defined")]
    public async Task Debug_traceCall_flatCallTracer_mux_keeps_js_step_and_fault_phases(string code, string expectedError)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        const string javascript = "{stepErrors:[],faultErrors:[],step:function(log){this.stepErrors.push(log.getError() || null);},fault:function(log){this.faultErrors.push(log.getError());},result:function(){return {stepErrors:this.stepErrors,faultErrors:this.faultErrors};}}";
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", FlatTransaction(), "latest", new
        {
            tracer = "muxTracer",
            tracerConfig = new Dictionary<string, object> { ["callTracer"] = new { }, ["flatCallTracer"] = new { }, [javascript] = new { } },
            stateOverrides = FlatOverrides(code)
        });
        JToken result = JToken.Parse(response)["result"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result["callTracer"]!["error"]!.Value<string>(), Is.EqualTo(expectedError));
            Assert.That(result["flatCallTracer"]![0]!["error"]!.Value<string>(), Is.EqualTo(expectedError));
            Assert.That(result[javascript]!["stepErrors"]!.ToObject<string?[]>(), Is.EqualTo(new string?[] { null }));
            Assert.That(result[javascript]!["faultErrors"]!.ToObject<string[]>(), Is.EqualTo(new[] { expectedError }));
        }
    }

    [Test]
    public async Task Debug_traceCall_flatCallTracer_filters_and_renumbers_precompiles([Values] bool includePrecompiles)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        string code = FlatCall(FlatPrecompile, "f1", true) + FlatCall(FlatPrecompile, "fa")
            + FlatCall(FlatPrecompile, "f2") + FlatCall(FlatPrecompile, "f4") + FlatCall(FlatChild, "f1") + "00";
        JToken rows = await TraceFlatCall(ctx, code, new { includePrecompiles, onlyTopCall = true, withLog = true });
        string[] expectedTypes = includePrecompiles ? ["call", "staticcall", "callcode", "delegatecall", "call"] : ["callcode", "delegatecall", "call"];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows.Count(), Is.EqualTo(expectedTypes.Length + 1));
            Assert.That(rows[0]!["subtraces"]!.Value<int>(), Is.EqualTo(expectedTypes.Length));
            for (int i = 0; i < expectedTypes.Length; i++)
            {
                Assert.That(rows[i + 1]!["action"]!["callType"]!.Value<string>(), Is.EqualTo(expectedTypes[i]));
                Assert.That(rows[i + 1]!["traceAddress"]!.ToObject<int[]>(), Is.EqualTo(new[] { i }));
            }
            if (includePrecompiles)
            {
                Assert.That(rows[1]!["action"]!["value"]!.Value<string>(), Is.EqualTo("0x1"));
                Assert.That(rows[2]!["action"]!["value"]!.Value<string>(), Is.EqualTo("0x0"));
            }
        }
    }

    [Test]
    public async Task Debug_traceCall_flatCallTracer_uses_fork_precompiles([Values] bool cancun)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(cancun ? (IReleaseSpec)Cancun.Instance : Shanghai.Instance));
        const string pointEvaluation = "0x000000000000000000000000000000000000000a";
        JToken rows = await TraceFlatCall(ctx, FlatCall(pointEvaluation, "f1") + "00", new { });
        Assert.That(rows.Count(), Is.EqualTo(cancun ? 1 : 2));
    }

    [Test]
    public async Task Debug_traceCall_flatCallTracer_filters_canonical_not_relocated_precompiles()
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        Dictionary<string, object> overrides = FlatOverrides(FlatCall(FlatPrecompile, "f1") + FlatCall(FlatChild, "f1") + "00");
        overrides.Remove(FlatChild);
        overrides[FlatPrecompile] = new { movePrecompileToAddress = FlatChild, code = "0x" };
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", FlatTransaction(), "latest",
            new { tracer = "flatCallTracer", stateOverrides = overrides });
        JToken rows = JToken.Parse(response)["result"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows.Count(), Is.EqualTo(2));
            Assert.That(rows[1]!["action"]!["to"]!.Value<string>(), Is.EqualTo(FlatChild));
            Assert.That(rows[1]!["traceAddress"]!.ToObject<int[]>(), Is.EqualTo(new[] { 0 }));
        }
    }

    [Test]
    public async Task Debug_traceCall_flatCallTracer_keeps_root_precompile([Values] bool includePrecompiles)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", FlatTransaction(FlatPrecompile), "latest",
            new { tracer = "flatCallTracer", tracerConfig = new { includePrecompiles } });
        JToken rows = JToken.Parse(response)["result"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows.Count(), Is.EqualTo(1));
            Assert.That(rows[0]!["action"]!["to"]!.Value<string>(), Is.EqualTo(FlatPrecompile));
        }
    }

    [TestCase("600060006000f05000", "create")]
    [TestCase("6000600060006000f55000", "create2")]
    public async Task Debug_traceCall_flatCallTracer_creation(string code, string method)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        JToken rows = await TraceFlatCall(ctx, code, new { });
        JToken frame = rows[1]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(frame["type"]!.Value<string>(), Is.EqualTo("create"));
            Assert.That(frame["action"]!["creationMethod"]!.Value<string>(), Is.EqualTo(method));
            Assert.That(frame["action"]!["init"]!.Value<string>(), Is.EqualTo("0x"));
            Assert.That(frame["action"]!["to"], Is.Null);
            Assert.That(frame["result"]!["code"]!.Value<string>(), Is.EqualTo("0x"));
            Assert.That(frame["result"]!["address"]!.Type, Is.EqualTo(JTokenType.String));
        }
    }

    [TestCase("60006000f3", "0x", false)]
    [TestCase("602a60005360016000fd", "0x2a", true)]
    public async Task Debug_traceCall_flatCallTracer_root_creation(string init, string output, bool reverted)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { from = FlatSender, gas = "0x186a0", data = "0x" + init }, "latest",
            new { tracer = "flatCallTracer", stateOverrides = FlatOverrides("00") });
        JToken frame = JToken.Parse(response)["result"]![0]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(frame["type"]!.Value<string>(), Is.EqualTo("create"));
            Assert.That(frame["action"]!["init"]!.Value<string>(), Is.EqualTo("0x" + init));
            Assert.That(frame["result"]!["code"]!.Value<string>(), Is.EqualTo(output));
            Assert.That(frame["traceAddress"]!.ToObject<int[]>(), Is.Empty);
            if (reverted)
            {
                Assert.That(frame["error"]!.Value<string>(), Is.EqualTo("execution reverted"));
                Assert.That(frame["result"]!["address"], Is.Null);
            }
        }
    }

    [Test]
    public async Task Debug_traceCall_flatCallTracer_nested_mux_preserves_call_tree()
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        Dictionary<string, object> overrides = FlatOverrides(FlatCall(FlatPrecompile, "f1") + FlatCall(FlatChild, "f1") + "00");
        overrides[FlatChild] = new { code = "0x" + FlatCall(FlatPrecompile, "f2") + "00" };
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", FlatTransaction(), "latest", new
        {
            tracer = "muxTracer",
            tracerConfig = new { muxTracer = new { flatCallTracer = new { }, callTracer = new { } } },
            stateOverrides = overrides
        });
        JToken result = JToken.Parse(response)["result"]!["muxTracer"]!;
        JToken flat = result["flatCallTracer"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(flat.Count(), Is.EqualTo(3));
            Assert.That(flat[1]!["traceAddress"]!.ToObject<int[]>(), Is.EqualTo(new[] { 0 }));
            Assert.That(flat[2]!["traceAddress"]!.ToObject<int[]>(), Is.EqualTo(new[] { 0, 0 }));
            Assert.That(result["callTracer"]!["calls"]!.Count(), Is.EqualTo(2));
            Assert.That(result["callTracer"]!["calls"]![0]!["to"]!.Value<string>(), Is.EqualTo(FlatPrecompile));
        }
    }

    [Test]
    public async Task Debug_traceTransaction_flatCallTracer_uses_transaction_context()
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        Block block = await AddTraceCallPrefixTransfers(ctx.Blockchain, 2);
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceTransaction",
            block.Transactions[1].Hash, new { tracer = "flatCallTracer" });
        JToken frame = JToken.Parse(response)["result"]![0]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(frame["blockHash"]!.Value<string>(), Is.EqualTo(block.Hash!.ToString()));
            Assert.That(frame["blockNumber"]!.Value<ulong>(), Is.EqualTo(block.Number));
            Assert.That(frame["transactionHash"]!.Value<string>(), Is.EqualTo(block.Transactions[1].Hash!.ToString()));
            Assert.That(frame["transactionPosition"]!.Value<int>(), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Debug_traceCall_flatCallTracer_selfdestruct()
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        JToken rows = await TraceFlatCall(ctx, "73" + FlatChild[2..] + "ff", new { });
        JToken expected = JToken.Parse($$"""
            {"action":{"address":"{{FlatTarget}}","balance":"0x100000000","refundAddress":"{{FlatChild}}"},
            "blockHash":null,"blockNumber":0,"subtraces":0,"traceAddress":[0],"transactionHash":null,"transactionPosition":0,"type":"suicide"}
            """);
        Assert.That(JToken.DeepEquals(rows[1], expected), Is.True, rows.ToString());
    }

    [TestCase("null")]
    [TestCase("{}")]
    [TestCase("{\"includePrecompiles\":null,\"convertParityErrors\":null}")]
    [TestCase("{\"onlyTopCall\":\"ignored\",\"withLog\":42,\"unknown\":true}")]
    public async Task Debug_traceCall_flatCallTracer_ignores_unknown_and_null_config(string config)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        JToken rows = await TraceFlatCall(ctx, "00", JsonSerializer.Deserialize<JsonElement>(config));
        Assert.That(rows[0]!["result"]!["gasUsed"]!.Value<string>(), Is.EqualTo("0x5208"));
    }

    [TestCase("[]", "json: cannot unmarshal array into Go value of type native.flatCallTracerConfig")]
    [TestCase("1", "json: cannot unmarshal number into Go value of type native.flatCallTracerConfig")]
    [TestCase("\"x\"", "json: cannot unmarshal string into Go value of type native.flatCallTracerConfig")]
    [TestCase("{\"includePrecompiles\":\"x\"}", "json: cannot unmarshal string into Go struct field flatCallTracerConfig.includePrecompiles of type bool")]
    public async Task Debug_traceCall_flatCallTracer_invalid_config(string config, string expected)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", FlatTransaction(), "latest",
            new { tracer = "flatCallTracer", tracerConfig = JsonSerializer.Deserialize<JsonElement>(config), stateOverrides = FlatOverrides("00") });
        JToken envelope = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(envelope["result"], Is.Null);
            Assert.That(envelope["error"]?["code"]?.Value<int>(), Is.EqualTo(-32000));
            Assert.That(envelope["error"]?["message"]?.Value<string>(), Is.EqualTo(expected));
        }
    }

    private static object FlatTransaction(string? to = FlatTarget) => new { from = FlatSender, to, gas = "0x186a0" };

    private static Dictionary<string, object> FlatOverrides(string code) => new()
    {
        [FlatSender] = new { balance = "0x100000000" },
        [FlatTarget] = new { code = "0x" + code, balance = "0x100000000" },
        [FlatChild] = new { code = "0x00" }
    };

    private static string FlatCall(string to, string opcode, bool value = false) =>
        "6000600060006000" + (opcode is "f1" or "f2" ? value ? "6001" : "6000" : "") + "73" + to[2..] + "61ffff" + opcode + "50";

    private static async Task<JToken> TraceFlatCall(Context ctx, string code, object config)
    {
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", FlatTransaction(), "latest",
            new { tracer = "flatCallTracer", tracerConfig = config, stateOverrides = FlatOverrides(code) });
        JToken envelope = JToken.Parse(response);
        Assert.That(envelope["error"], Is.Null, response);
        return envelope["result"]!;
    }
}
