// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Test.Builders;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    public static IEnumerable<TestCaseData> LegacyTimeoutValidationMethods()
    {
        string hash = TestItem.KeccakA.ToString();
        (string Method, object[] Arguments)[] methods =
        [
            ("debug_traceTransaction", [hash]),
            ("debug_traceTransactionByBlockhashAndIndex", [hash, 0]),
            ("debug_traceTransactionByBlockAndIndex", ["latest", 0]),
            ("debug_traceTransactionInBlockByHash", ["0x", hash]),
            ("debug_traceTransactionInBlockByIndex", ["0x", 0]),
            ("debug_traceBlock", ["0x"]),
            ("debug_traceBlockByNumber", ["latest"]),
            ("debug_traceBlockByHash", [hash]),
            ("debug_intermediateRoots", [hash]),
            ("debug_standardTraceBlockToFile", [hash]),
            ("debug_standardTraceBadBlockToFile", [hash]),
            ("debug_simulateV1", [new { blockStateCalls = Array.Empty<object>() }, "latest"]),
            ("debug_traceCallMany", [Array.Empty<object>(), "latest"])
        ];
        foreach ((string method, object[] arguments) in methods)
            yield return new TestCaseData(method, arguments);
    }

    [TestCaseSource(nameof(LegacyTimeoutValidationMethods))]
    public async Task Debug_legacy_timeout_validation_rejects_invalid_duration(string method, object[] arguments)
    {
        using Context ctx = await Context.Create();
        foreach (string timeout in new[] { "bad", "00:00:05" })
            foreach (string? tracer in new[] { null, "callTracer", "unknownTracer" })
                foreach (bool streamMode in new[] { false, true })
                {
                    object[] parameters = [.. arguments, new { timeout, tracer, streamMode }];
                    JToken response = JToken.Parse(await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, method, parameters));
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(response["result"], Is.Null, response.ToString());
                        Assert.That(response["error"]!["code"]!.Value<int>(), Is.EqualTo(-32602), response.ToString());
                        Assert.That(response["error"]!["data"]!.Value<string>(), Does.Contain("time:"), response.ToString());
                    }
                }
    }

    [Test]
    public async Task Debug_legacy_timeout_validation_does_not_start_a_deadline(
        [Values("0", "-1s", "1ns", "3m")] string timeout)
    {
        using Context ctx = await Context.Create();
        JToken response = JToken.Parse(await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCallMany",
            Array.Empty<object>(), "latest", new { timeout }));
        Assert.That(response["error"], Is.Null, response.ToString());
        Assert.That(response["result"], Is.EqualTo(new JArray()));
    }
}
