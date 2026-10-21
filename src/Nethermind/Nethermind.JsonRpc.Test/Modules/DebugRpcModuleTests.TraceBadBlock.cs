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
    [Test]
    public async Task Debug_traceBadBlock_replays_rejected_block_on_its_parent_state(
        [Values] bool streamMode, [Values(null, "callTracer", "{fault:function(){},result:function(){throw Error('result failure');}}")] string? tracer)
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

        Assert.That(JToken.Parse(actual)["error"], Is.Null, actual);
        Assert.That((JArray)JToken.Parse(actual)["result"]!, Has.Count.EqualTo(valid.Transactions.Length));
        Assert.That(actual, Is.EqualTo(expected));
    }
}
