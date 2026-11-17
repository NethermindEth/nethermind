// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
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
    public async Task Trace_options_null_booleans_are_ignored(
        [Values("debug_traceCall", "debug_traceBadBlock", "debug_standardTraceBlockToFile", "debug_standardTraceBadBlockToFile")] string method)
    {
        using Context context = await Context.Create();
        JToken json = await TraceWithOptions(context, method,
            """{"disableStorage":null,"disableStack":null,"enableMemory":null,"enableReturnData":null}""");

        Assert.That(json["error"], Is.Null, json.ToString());
    }

    [Test]
    public async Task Trace_options_malformed_timeout_is_a_trace_error([Values("debug_traceCall", "debug_traceBadBlock")] string method)
    {
        using Context context = await Context.Create();
        JToken json = await TraceWithOptions(context, method, """{"timeout":"zz"}""");

        using (Assert.EnterMultipleScope())
        {
            Assert.That((int?)json["error"]?["code"], Is.EqualTo(ErrorCodes.InvalidInput), json.ToString());
            Assert.That((string?)json["error"]?["message"], Does.Contain("'zz'"), json.ToString());
        }
    }

    private static async Task<JToken> TraceWithOptions(Context context, string method, string options)
    {
        using JsonDocument document = JsonDocument.Parse(options);
        JsonElement element = document.RootElement;
        (Block valid, Block bad) = await AddValidAndBadBlock(context);
        string response = method switch
        {
            "debug_traceCall" => await RpcTest.TestSerializedRequest(context.DebugRpcModule, method,
                new { to = TestItem.AddressC.ToString() }, "latest", element),
            "debug_traceBadBlock" or "debug_standardTraceBadBlockToFile" => await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, bad.Hash!, element),
            _ => await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, valid.Hash!, element)
        };

        JToken json = JToken.Parse(response);
        if (method.StartsWith("debug_standardTrace") && json["result"] is JArray files)
            foreach (JToken file in files)
                File.Delete((string)file!);
        return json;
    }
}
