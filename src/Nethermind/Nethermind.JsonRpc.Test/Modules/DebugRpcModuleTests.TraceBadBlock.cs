// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
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
        (Block valid, Block bad) = await AddValidAndBadBlock(context);

        string expected = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByHash", valid.Hash!, new { tracer, streamMode });
        string actual = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBadBlock", bad.Hash!, new { tracer, streamMode });

        JToken json = JToken.Parse(actual);
        Assert.That(json["error"], Is.Null, actual);
        JArray entries = (JArray)json["result"]!;
        if (tracer is ThrowingResultJavaScriptTracer)
        {
            JToken expectedJson = JToken.Parse(expected);
            foreach (JObject entry in expectedJson["result"]!)
            {
                Assert.That(entry["result"]?.Type, Is.EqualTo(JTokenType.Null));
                entry.Remove("result");
            }
            expected = expectedJson.ToString(Newtonsoft.Json.Formatting.None);
            foreach (JToken entry in entries)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That((string?)entry["error"] ?? "", Does.Contain("result failure"), actual);
                    Assert.That(entry["result"], Is.Null, actual);
                }
            }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries, Has.Count.EqualTo(valid.Transactions.Length));
            Assert.That(actual, Is.EqualTo(expected));
        }
    }

    [Test]
    public async Task Debug_traceBadBlock_without_options_streams_struct_logs([Values] bool withBlockAccessList)
    {
        using Context context = withBlockAccessList
            ? await Context.Create(new TestSpecProvider(Amsterdam.Instance) { AllowTestChainOverride = false })
            : await Context.Create();
        (Block valid, Block bad) = await AddValidAndBadBlock(context);
        Assert.That(context.Blockchain.Container.Resolve<IBadBlockStore>().GetAll().Single().EncodedBlockAccessList, withBlockAccessList ? Is.Not.Null : Is.Null);

        string expected = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByHash", valid.Hash!);
        string actual = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBadBlock", bad.Hash!);

        Assert.That(actual, Is.EqualTo(expected));
        JArray entries = (JArray)JToken.Parse(actual)["result"]!;
        Assert.That(entries, Has.Count.EqualTo(valid.Transactions.Length), actual);
        foreach (JToken entry in entries)
        {
            Assert.That(entry["error"], Is.Null, actual);
            AssertStructLogsTraced(entry["result"]!, actual);
        }
    }

    private static async Task<(Block Valid, Block Bad)> AddValidAndBadBlock(Context context)
    {
        Block valid = await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        BlockHeader badHeader = valid.Header.Clone();
        badHeader.StateRoot = TestItem.KeccakH;
        badHeader.Hash = badHeader.CalculateHash();
        Block bad = valid.WithReplacedHeader(badHeader);
        bad.BlockAccessList = context.Blockchain.Container.Resolve<IBlockAccessListStore>().Get(valid.Number, valid.Hash!);
        context.Blockchain.Container.Resolve<IBadBlockStore>().Insert(bad);
        return (valid, bad);
    }
}
