// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    [Test]
    public async Task Debug_traceTransaction_rejects_prestate_diff_with_include_empty_as_trace_error()
    {
        using Context context = await Context.Create();
        Block block = await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));

        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransaction", block.Transactions[0].Hash!,
            new { tracer = "prestateTracer", tracerConfig = new { diffMode = true, includeEmpty = true } });

        JToken error = JToken.Parse(response)["error"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int?)error["code"], Is.EqualTo(ErrorCodes.InvalidInput), response);
            Assert.That((string?)error["message"], Is.EqualTo("cannot use diffMode with includeEmpty"), response);
        }
    }

    [TestCase(null, "{}")]
    [TestCase("null", "null")]
    [TestCase("\"abc\"", "\"abc\"")]
    [TestCase("true", "true")]
    [TestCase("1.50", "1.50")]
    [TestCase("""{"a":[1,"b"]}""", """{"a":[1,"b"]}""")]
    public async Task JavaScript_setup_receives_raw_tracer_config(string? tracerConfig, string expected)
    {
        using Context context = await Context.Create();
        Block block = await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        const string tracer = "{config:null,setup:function(c){this.config=c;},fault:function(){},result:function(){return this.config;}}";
        string configMember = tracerConfig is null ? "" : $",\"tracerConfig\":{tracerConfig}";
        using JsonDocument options = JsonDocument.Parse($"{{\"tracer\":{JsonSerializer.Serialize(tracer)}{configMember}}}");

        string traceCall = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceCall",
            new { to = TestItem.AddressC.ToString() }, "latest", options.RootElement);
        string traceTransaction = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransaction",
            block.Transactions[0].Hash!, options.RootElement);

        using (Assert.EnterMultipleScope())
        {
            foreach (string response in new[] { traceCall, traceTransaction })
            {
                JToken json = JToken.Parse(response);
                Assert.That(json["error"], Is.Null, response);
                Assert.That((string?)json["result"], Is.EqualTo(expected), response);
            }
        }
    }
}
