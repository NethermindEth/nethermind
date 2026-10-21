// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    private const string ThrowingResultJavaScriptTracer = "{fault:function(){},result:function(){throw Error('result failure');}}";

    [Test]
    public async Task Debug_traceBadBlock_replays_rejected_block_on_its_parent_state(
        [Values] bool streamMode, [Values(null, "callTracer", ThrowingResultJavaScriptTracer)] string? tracer)
    {
        using Context context = await Context.Create();
        Block valid = await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        BlockHeader badHeader = valid.Header.Clone();
        badHeader.StateRoot = TestItem.KeccakH;
        badHeader.Hash = badHeader.CalculateHash();
        Block bad = valid.WithReplacedHeader(badHeader);
        context.Blockchain.Container.Resolve<IBadBlockStore>().Insert(bad);

        string expected = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByHash", valid.Hash!, new { tracer, streamMode });
        string actual = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBadBlock", bad.Hash!, new { tracer, streamMode });

        JToken json = JToken.Parse(actual);
        Assert.That(json["error"], Is.Null, actual);
        JArray entries = (JArray)json["result"]!;
        Assert.That(entries, Has.Count.EqualTo(valid.Transactions.Length));
        Assert.That(actual, Is.EqualTo(expected));
        if (tracer is ThrowingResultJavaScriptTracer)
        {
            foreach (JToken entry in entries)
            {
                Assert.That((string?)entry["error"] ?? "", Does.Contain("result failure"), actual);
            }
        }
    }
}
