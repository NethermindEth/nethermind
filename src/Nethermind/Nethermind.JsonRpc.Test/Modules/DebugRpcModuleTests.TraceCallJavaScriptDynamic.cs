// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    private static IEnumerable<TestCaseData> JavaScriptDynamicOpcodeCases()
    {
        yield return new TestCaseData("602060002000", "0x186a0", "{\"events\":[{\"kind\":\"step\",\"op\":\"KECCAK256\",\"cost\":39,\"error\":null,\"memory\":0,\"stack\":2}],\"error\":null}", null, false).SetName("Debug_traceCall_javascript_dynamic_hash");
        yield return new TestCaseData("602060002000", "0x522d", "{\"events\":[{\"kind\":\"step\",\"op\":\"KECCAK256\",\"cost\":39,\"error\":\"out of gas\",\"memory\":0,\"stack\":2}],\"error\":\"out of gas\"}", null, false).SetName("Debug_traceCall_javascript_dynamic_hash-lowGas");
        yield return new TestCaseData("61010060020a00", "0x186a0", "{\"events\":[{\"kind\":\"step\",\"op\":\"EXP\",\"cost\":110,\"error\":null,\"memory\":0,\"stack\":2}],\"error\":null}", null, false).SetName("Debug_traceCall_javascript_dynamic_exp");
        yield return new TestCaseData("61010060020a00", "0x5219", "{\"events\":[{\"kind\":\"step\",\"op\":\"EXP\",\"cost\":110,\"error\":\"out of gas\",\"memory\":0,\"stack\":2}],\"error\":\"out of gas\"}", null, false).SetName("Debug_traceCall_javascript_dynamic_exp-lowGas");
        yield return new TestCaseData("60206000a000", "0x186a0", "{\"events\":[{\"kind\":\"step\",\"op\":\"LOG0\",\"cost\":634,\"error\":null,\"memory\":0,\"stack\":2}],\"error\":null}", null, false).SetName("Debug_traceCall_javascript_dynamic_log0");
        yield return new TestCaseData("60206000a000", "0x5386", "{\"events\":[{\"kind\":\"step\",\"op\":\"LOG0\",\"cost\":634,\"error\":\"out of gas\",\"memory\":0,\"stack\":2}],\"error\":\"out of gas\"}", null, false).SetName("Debug_traceCall_javascript_dynamic_log0-lowGas");
        yield return new TestCaseData("6020600060205e00", "0x186a0", "{\"events\":[{\"kind\":\"step\",\"op\":\"MCOPY\",\"cost\":12,\"error\":null,\"memory\":0,\"stack\":3}],\"error\":null}", null, false).SetName("Debug_traceCall_javascript_dynamic_mcopy");
        yield return new TestCaseData("6020600060205e00", "0x5215", "{\"events\":[{\"kind\":\"step\",\"op\":\"MCOPY\",\"cost\":12,\"error\":\"out of gas\",\"memory\":0,\"stack\":3}],\"error\":\"out of gas\"}", null, false).SetName("Debug_traceCall_javascript_dynamic_mcopy-lowGas");
        yield return new TestCaseData("60206000600060013c00", "0x186a0", "{\"events\":[{\"kind\":\"step\",\"op\":\"EXTCODECOPY\",\"cost\":106,\"error\":null,\"memory\":0,\"stack\":4}],\"error\":null}", null, false).SetName("Debug_traceCall_javascript_dynamic_extcodecopy");
        yield return new TestCaseData("60206000600060013c00", "0x5279", "{\"events\":[{\"kind\":\"step\",\"op\":\"EXTCODECOPY\",\"cost\":106,\"error\":\"out of gas\",\"memory\":0,\"stack\":4}],\"error\":\"out of gas\"}", null, false).SetName("Debug_traceCall_javascript_dynamic_extcodecopy-lowGas");
        yield return new TestCaseData("60017fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff2000", "0x186a0", "{\"events\":[{\"kind\":\"step\",\"op\":\"KECCAK256\",\"cost\":30,\"error\":\"gas uint64 overflow\",\"memory\":0,\"stack\":2}],\"error\":\"gas uint64 overflow\"}", null, false).SetName("Debug_traceCall_javascript_dynamic_hash-overflow");
        yield return new TestCaseData("7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff60002000", "0x186a0", "{\"events\":[{\"kind\":\"step\",\"op\":\"KECCAK256\",\"cost\":30,\"error\":\"gas uint64 overflow\",\"memory\":0,\"stack\":2}],\"error\":\"gas uint64 overflow\"}", null, false).SetName("Debug_traceCall_javascript_dynamic_hash-length-overflow");
        yield return new TestCaseData("600160007fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff5e00", "0x186a0", "{\"events\":[{\"kind\":\"step\",\"op\":\"MCOPY\",\"cost\":3,\"error\":\"gas uint64 overflow\",\"memory\":0,\"stack\":3}],\"error\":\"gas uint64 overflow\"}", null, false).SetName("Debug_traceCall_javascript_dynamic_mcopy-overflow");
        yield return new TestCaseData("60017fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffa000", "0x186a0", "{\"events\":[{\"kind\":\"step\",\"op\":\"LOG0\",\"cost\":0,\"error\":\"gas uint64 overflow\",\"memory\":0,\"stack\":2}],\"error\":\"gas uint64 overflow\"}", null, false).SetName("Debug_traceCall_javascript_dynamic_log-overflow");
        yield return new TestCaseData("600160007fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff60013c00", "0x186a0", "{\"events\":[{\"kind\":\"step\",\"op\":\"EXTCODECOPY\",\"cost\":100,\"error\":\"gas uint64 overflow\",\"memory\":0,\"stack\":4}],\"error\":\"gas uint64 overflow\"}", null, false).SetName("Debug_traceCall_javascript_dynamic_extcopy-overflow");
        yield return new TestCaseData("60007fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff2000", "0x522c", "{\"events\":[{\"kind\":\"step\",\"op\":\"KECCAK256\",\"cost\":30,\"error\":null,\"memory\":0,\"stack\":2}],\"error\":null}", null, false).SetName("Debug_traceCall_javascript_dynamic_hash-empty");
        yield return new TestCaseData("600060002000", "0x522b", "{\"events\":[{\"kind\":\"step\",\"op\":\"KECCAK256\",\"cost\":30,\"error\":\"out of gas\",\"memory\":0,\"stack\":2}],\"error\":\"out of gas\"}", null, false).SetName("Debug_traceCall_javascript_dynamic_hash-constant-oog");
        yield return new TestCaseData("600060000a00", "0x5217", "{\"events\":[{\"kind\":\"step\",\"op\":\"EXP\",\"cost\":10,\"error\":\"out of gas\",\"memory\":0,\"stack\":2}],\"error\":\"out of gas\"}", null, false).SetName("Debug_traceCall_javascript_dynamic_exp-constant-oog");
        yield return new TestCaseData("600060010a00", "0x5218", "{\"events\":[{\"kind\":\"step\",\"op\":\"EXP\",\"cost\":10,\"error\":null,\"memory\":0,\"stack\":2}],\"error\":null}", null, false).SetName("Debug_traceCall_javascript_dynamic_exp-zero");
        yield return new TestCaseData("60007fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff5e00", "0x5214", "{\"events\":[{\"kind\":\"step\",\"op\":\"MCOPY\",\"cost\":3,\"error\":null,\"memory\":0,\"stack\":3}],\"error\":null}", null, false).SetName("Debug_traceCall_javascript_dynamic_mcopy-empty");
        yield return new TestCaseData("60006000a100", "0x1d8a8", "{\"events\":[{\"kind\":\"step\",\"op\":\"LOG1\",\"cost\":0,\"error\":\"stack underflow (2 <=> 3)\",\"memory\":0,\"stack\":2}],\"error\":\"stack underflow (2 <=> 3)\"}", null, false).SetName("Debug_traceCall_javascript_dynamic_log1-underflow");
        yield return new TestCaseData("600060006000600061100261c350fa00", "0x1d8a8", "{\"events\":[{\"kind\":\"step\",\"op\":\"STATICCALL\",\"cost\":52600,\"error\":null,\"memory\":0,\"stack\":6},{\"kind\":\"step\",\"op\":\"LOG0\",\"cost\":634,\"error\":null,\"memory\":0,\"stack\":2},{\"kind\":\"fault\",\"op\":\"LOG0\",\"cost\":634,\"error\":\"write protection\",\"memory\":0,\"stack\":2}],\"error\":null}", "0x60206000a000", false).SetName("Debug_traceCall_javascript_dynamic_static-log");
        yield return new TestCaseData("6001ff", "0x1d8a8", "{\"events\":[{\"kind\":\"step\",\"op\":\"SELFDESTRUCT\",\"cost\":5000,\"refund\":24000,\"error\":null,\"memory\":0,\"stack\":1}],\"error\":null}", "0x6001ff", true).SetName("Debug_traceCall_javascript_dynamic_refund-root");
        yield return new TestCaseData("6000600060006000600061100261c350f100", "0x1d8a8", "{\"events\":[{\"kind\":\"step\",\"op\":\"CALL\",\"cost\":52600,\"refund\":0,\"error\":null,\"memory\":0,\"stack\":7},{\"kind\":\"step\",\"op\":\"SELFDESTRUCT\",\"cost\":5000,\"refund\":24000,\"error\":null,\"memory\":0,\"stack\":1},{\"kind\":\"step\",\"op\":\"STOP\",\"cost\":0,\"refund\":24000,\"error\":null,\"memory\":0,\"stack\":1}],\"error\":null}", "0x6001ff", true).SetName("Debug_traceCall_javascript_dynamic_refund-child");
    }

    [TestCaseSource(nameof(JavaScriptDynamicOpcodeCases))]
    public async Task Debug_traceCall_javascript_dynamic_opcodes(string code, string gas, string expected, string? childCode, bool legacyRefunds)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(legacyRefunds ? Berlin.Instance : Cancun.Instance));
        string tracer = "{events:[],capture:function(kind,log){var op=log.op.toString();if(op.indexOf(\"PUSH\")===0||op===\"STOP\"||op===\"POP\")return;this.events.push({kind:kind,op:op,cost:log.getCost(),error:log.getError()||null,memory:log.memory.length(),stack:log.stack.length()});},step:function(log){this.capture(\"step\",log);},fault:function(log){this.capture(\"fault\",log);},result:function(ctx){return {events:this.events,error:ctx.error||null};}}";
        if (legacyRefunds)
            tracer = tracer.Replace("cost:log.getCost(),", "cost:log.getCost(),refund:log.getRefund(),")
                .Replace("||op===\"STOP\"||op===\"POP\"", "||op===\"POP\"");
        Dictionary<string, object> stateOverrides = new()
        {
            ["0x0000000000000000000000000000000000001001"] = new { code = "0x" + code }
        };
        if (childCode is not null)
            stateOverrides["0x0000000000000000000000000000000000001002"] = new { code = childCode };
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { from = "0x0000000000000000000000000000000000001000", to = "0x0000000000000000000000000000000000001001", gas },
            "latest", new
            {
                tracer,
                stateOverrides
            });
        JToken json = JToken.Parse(response);
        Assert.That(json["error"], Is.Null, response);
        Assert.That(JToken.DeepEquals(json["result"], JToken.Parse(expected)), Is.True, response);
    }
}
