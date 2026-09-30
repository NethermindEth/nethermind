// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    private const string ErcSlot = "0x0000000000000000000000000000000000000000000000000000000000000000";
    private const string ErcValue = "0x000000000000000000000000000000000000000000000000000000000000002a";

    [Test]
    public async Task Debug_traceCall_erc7562Tracer_recorded_controls(
        [Values("stop", "sload", "callcode")] string scenario, [Values] bool mux, [Values(null, "0x0")] string? txIndex)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        string code = scenario switch
        {
            "sload" => "6000545060005400",
            "callcode" => "6000600060006000600073" + FlatChild[2..] + "61fffff200",
            _ => "00"
        };
        Dictionary<string, object> overrides = ErcOverrides(code, "60005400");
        JToken result = await TraceErc(ctx, overrides, mux: mux, txIndex: txIndex);
        JObject expected = ErcEmptyFrame("CALL", FlatSender, FlatTarget, "0x186a0", scenario switch { "sload" => "0x5aa8", "callcode" => "0x647c", _ => "0x5208" });
        if (scenario == "sload")
        {
            expected["usedOpcodes"]!["0x54"] = 2;
            expected["accessedSlots"]!["reads"]![ErcSlot] = new JArray(ErcValue);
        }
        if (scenario == "callcode")
        {
            expected["usedOpcodes"]!["0xf2"] = 1;
            expected["contractSize"]![FlatChild] = new JObject { ["contractSize"] = 4, ["opcode"] = 242 };
            JObject child = ErcEmptyFrame("CALLCODE", FlatTarget, FlatChild, "0xffff", "0x837");
            child["usedOpcodes"]!["0x54"] = 1;
            child["accessedSlots"]!["reads"]![ErcSlot] = new JArray(ErcValue);
            expected["calls"] = new JArray(child);
        }
        Assert.That(JToken.DeepEquals(result, expected), Is.True, result.ToString());
    }

    [TestCase("6001", "{}", "{\"0x0\":1}")]
    [TestCase("600100", "{\"ignoredOpcodes\":[]}", "{\"0x60\":1,\"0x0\":1}")]
    [TestCase("600100", "{\"ignoredOpcodes\":null}", "{\"0x0\":1}")]
    [TestCase("600100", "{\"ignoredOpcodes\":[\"0x0\"]}", "{\"0x60\":1}")]
    [TestCase("600100", "{\"ignoredOpcodes\":[\"0x100\"]}", "{\"0x60\":1}")]
    [TestCase("600100", "{\"stackTopItemsSize\":0}", "{\"0x0\":1}")]
    public async Task Debug_traceCall_erc7562Tracer_opcode_config_and_implicit_stop(string code, string config, string expected)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        JToken result = await TraceErc(ctx, ErcOverrides(code), JsonSerializer.Deserialize<JsonElement>(config));
        Assert.That(JToken.DeepEquals(result["usedOpcodes"], JToken.Parse(expected)), Is.True, result.ToString());
    }

    [Test]
    public async Task Debug_traceCall_erc7562Tracer_storage_attempts_and_write_before_read()
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        JToken result = await TraceErc(ctx, ErcOverrides("600160005560005450600260005d60005c5000"));
        JToken slots = result["accessedSlots"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(slots["reads"]!.Count(), Is.Zero);
            Assert.That(slots["writes"]![ErcSlot]!.Value<int>(), Is.EqualTo(1));
            Assert.That(slots["transientReads"]![ErcSlot]!.Value<int>(), Is.EqualTo(1));
            Assert.That(slots["transientWrites"]![ErcSlot]!.Value<int>(), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Debug_traceCall_erc7562Tracer_lookbehind([Values] bool suppressExt, [Values] bool returnAfterGas)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        string code = "73" + FlatChild[2..] + "3b" + (suppressExt ? "15" : "50") + "600060005a" + (returnAfterGas ? "f3" : "00");
        JToken result = await TraceErc(ctx, ErcOverrides(code));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result["extCodeAccessInfo"]!.Count(), Is.EqualTo(suppressExt ? 0 : 1));
            Assert.That(result["usedOpcodes"]!["0x5a"]?.Value<int>(), Is.EqualTo(returnAfterGas ? (int?)null : 1));
            Assert.That(result["contractSize"]![FlatChild]!["opcode"]!.Value<int>(), Is.EqualTo(59));
        }
    }

    [Test]
    public async Task Debug_traceCall_erc7562Tracer_lookbehind_is_transaction_global()
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        // A failing EXTCODECOPY has no following child opcode; the parent sees its lookbehind.
        string childCode = "600160007f" + new string('f', 64) + "73" + FlatChild[2..] + "3c";
        JToken result = await TraceErc(ctx, ErcOverrides(FlatCall(FlatChild, "f1") + "00", childCode));
        Assert.That(result["extCodeAccessInfo"]!.Count(), Is.EqualTo(1), result.ToString());
    }

    [Test]
    public async Task Debug_traceCall_erc7562Tracer_keccak_sorted_unique_and_reverted()
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        string hashOne = "600160002050";
        string code = "6002600053" + hashOne + "6001600053" + hashOne + hashOne + "60006000fd";
        JToken result = await TraceErc(ctx, ErcOverrides(code));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result["keccak"]!.ToObject<string[]>(), Is.EqualTo(new[] { "0x01", "0x02" }));
            Assert.That(result["error"]!.Value<string>(), Is.EqualTo("execution reverted"));
        }
    }

    [Test]
    public async Task Debug_traceCall_erc7562Tracer_logs_cleanup([Values] bool withLog, [Values] bool childReverts)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        const string log = "60006000a0";
        string child = log + (childReverts ? "60006000fd" : "00");
        JToken result = await TraceErc(ctx, ErcOverrides(log + FlatCall(FlatChild, "f1") + log + "00", child), new { withLog });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result["logs"]?.Count() ?? 0, Is.EqualTo(withLog ? 2 : 0));
            Assert.That(result["calls"]![0]!["logs"]?.Count() ?? 0, Is.EqualTo(withLog && !childReverts ? 1 : 0));
            if (withLog)
            {
                Assert.That(result["logs"]![1]!["position"]!.Value<string>(), Is.EqualTo("0x1"));
                Assert.That(result["logs"]![1]!["index"]!.Value<string>(), Is.EqualTo(childReverts ? "0x1" : "0x2"));
            }
        }
    }

    [Test]
    public async Task Debug_traceCall_erc7562Tracer_rejected_call_has_empty_metadata()
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        JToken result = await TraceErc(ctx, ErcOverrides(FlatCall(FlatChild, "f1", value: true) + "00"));
        JToken child = result["calls"]![0]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(child["error"]!.Value<string>(), Is.EqualTo("insufficient balance for transfer"));
            Assert.That(child["usedOpcodes"]!.Count(), Is.Zero);
            Assert.That(child["accessedSlots"]!["reads"]!.Count(), Is.Zero);
            Assert.That(child["outOfGas"]!.Value<bool>(), Is.False);
        }
    }

    [Test]
    public async Task Debug_traceCall_erc7562Tracer_code_deposit_out_of_gas(
        [Values("Frontier", "Homestead", "Cancun")] string fork,
        [Values] bool rootCreate, [Values] bool mux, [Values(-1, 0, 1, 512)] int codeSize)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(fork switch
        {
            "Frontier" => Frontier.Instance,
            "Homestead" => Homestead.Instance,
            _ => Cancun.Instance
        }));
        string init = codeSize < 0 ? "000000000000" : "61" + codeSize.ToString("x4") + "6000f3";
        string factory = "65" + init + "6000526006601a6000f000";
        Dictionary<string, object> tx = new() { ["from"] = FlatSender, ["gas"] = "0x186a0" };
        if (rootCreate) tx["data"] = "0x" + init;
        else tx["to"] = FlatTarget;
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", tx, "latest", new
        {
            tracer = mux ? "muxTracer" : "erc7562Tracer",
            tracerConfig = mux ? new Dictionary<string, object> { ["erc7562Tracer"] = new { }, ["callTracer"] = new { } } : null,
            stateOverrides = ErcOverrides(factory)
        });
        JToken envelope = JToken.Parse(response);
        Assert.That(envelope["error"], Is.Null, response);
        JToken result = mux ? envelope["result"]!["erc7562Tracer"]! : envelope["result"]!;
        JToken creation = rootCreate ? result : result["calls"]![0]!;
        bool failed = codeSize == 512 && fork != "Frontier";
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result["outOfGas"]!.Value<bool>(), Is.False);
            Assert.That(creation["outOfGas"]!.Value<bool>(), Is.EqualTo(!rootCreate && codeSize == 512));
            Assert.That(creation["error"] is not null, Is.EqualTo(failed));
            Assert.That(creation["to"] is not null, Is.EqualTo(!failed));
            if (!rootCreate && fork == "Frontier" && codeSize == 512)
            {
                Assert.That(creation["gasUsed"]!.Value<string>(), Is.EqualTo("0x36"));
            }
            if (mux)
            {
                JToken native = envelope["result"]!["callTracer"]!;
                if (!rootCreate) native = native["calls"]![0]!;
                Assert.That(creation["gasUsed"]!.Value<string>(), Is.EqualTo(native["gasUsed"]!.Value<string>()));
                Assert.That(creation["error"]?.Value<string>(), Is.EqualTo(native["error"]?.Value<string>()));
                Assert.That(creation["to"]?.Value<string>(), Is.EqualTo(native["to"]?.Value<string>()));
            }
        }
    }

    [TestCase("{\"withLog\":1}", "json: cannot unmarshal number into Go struct field erc7562TracerConfig.withLog of type bool")]
    [TestCase("[]", "json: cannot unmarshal array into Go value of type native.erc7562TracerConfig")]
    [TestCase("{\"stackTopItemsSize\":-1}", "stackTopItemsSize must not be negative")]
    public async Task Debug_traceCall_erc7562Tracer_invalid_config_is_safe(string config, string error)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", FlatTransaction(), "latest", new
        {
            tracer = "erc7562Tracer",
            tracerConfig = JsonSerializer.Deserialize<JsonElement>(config),
            stateOverrides = ErcOverrides("00")
        });
        Assert.That(JToken.Parse(response)["error"]!["message"]!.Value<string>(), Is.EqualTo(error), response);
    }

    private static Dictionary<string, object> ErcOverrides(string code, string child = "00") => new()
    {
        [FlatSender] = new { balance = "0x100000000" },
        [FlatTarget] = new { code = "0x" + code, balance = "0x0", state = new Dictionary<string, string> { [ErcSlot] = ErcValue } },
        [FlatChild] = new { code = "0x" + child, balance = "0x0", state = new Dictionary<string, string> { [ErcSlot] = "0x" + new string('0', 62) + "99" } }
    };
    private static JObject ErcEmptyFrame(string type, string from, string to, string gas, string gasUsed) => new()
    {
        ["type"] = type,
        ["from"] = from,
        ["to"] = to,
        ["gas"] = gas,
        ["gasUsed"] = gasUsed,
        ["input"] = "0x",
        ["value"] = "0x0",
        ["accessedSlots"] = new JObject { ["reads"] = new JObject(), ["writes"] = new JObject(), ["transientReads"] = new JObject(), ["transientWrites"] = new JObject() },
        ["usedOpcodes"] = new JObject { ["0x0"] = 1 },
        ["extCodeAccessInfo"] = new JArray(),
        ["contractSize"] = new JObject(),
        ["outOfGas"] = false
    };
    private static async Task<JToken> TraceErc(Context ctx, Dictionary<string, object> overrides, object? config = null, bool mux = false, string? txIndex = null)
    {
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", FlatTransaction(), "latest", new
        {
            tracer = mux ? "muxTracer" : "erc7562Tracer",
            tracerConfig = mux ? new Dictionary<string, object?> { ["erc7562Tracer"] = config ?? new { }, ["callTracer"] = new { } } : config,
            stateOverrides = overrides,
            txIndex,
            blockOverrides = new { feeRecipient = FlatSender }
        });
        JToken envelope = JToken.Parse(response);
        Assert.That(envelope["error"], Is.Null, response);
        return mux ? envelope["result"]!["erc7562Tracer"]! : envelope["result"]!;
    }
}
