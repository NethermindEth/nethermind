// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    [Test]
    public async Task NativeConfigPreflight_matches_constructor_null_semantics(
        [Values("debug_traceBlockByNumber", "debug_traceBlockByHash", "debug_traceBlock")] string method,
        [Values("callTracer", "prestateTracer")] string tracer,
        [Values("null", "{\"withLog\":null,\"diffMode\":null}",
            "{\"onlyTopCall\":true,\"onlyTopCall\":null,\"diffMode\":true,\"diffMode\":null}")] string configuration)
    {
        using Context context = await Context.Create();
        Block block = await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        object selector = method switch
        {
            "debug_traceBlockByNumber" => block.Number,
            "debug_traceBlockByHash" => block.Hash!,
            _ => Rlp.Encode(block).ToString()
        };
        JsonElement tracerConfig = JsonSerializer.Deserialize<JsonElement>(configuration);
        string buffered = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, selector,
            new { tracer, tracerConfig, streamMode = false });
        string streamed = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, selector,
            new { tracer, tracerConfig, streamMode = true });

        Assert.That(JToken.Parse(buffered)["error"], Is.Null, buffered);
        Assert.That(streamed, Is.EqualTo(buffered));
        Assert.That((JArray)JToken.Parse(streamed)["result"]!, Has.Count.EqualTo(block.Transactions.Length));
    }
}
