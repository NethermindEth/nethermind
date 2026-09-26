// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.JsonRpc.Test;
using Nethermind.Specs;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>
/// Trace rewards come from the merge-aware reward calculator that block processing uses, so a PoW block keeps its
/// block reward record and a PoS block has none.
/// </summary>
public class MergeTraceRewardTests
{
    [Test]
    public async Task Trace_reports_the_block_reward_only_before_the_merge(
        [Values("trace_block", "trace_filter")] string method, [Values] bool isPostMerge, [Values] bool streaming)
    {
        ISpecProvider specProvider = MainnetSpecProvider.Instance;
        BlockTree blockTree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithSpecProvider(specProvider)
            .TestObject;

        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.IsPostMerge(Arg.Any<BlockHeader>()).Returns(isPostMerge);

        await using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Build.A.ChainSpec
                .WithAllocation(new Address("0xdea60e4f8ea50d5ed92b0a5b15ae9d24aeba0bee"), 1.Ether)
                .TestObject))
            .AddSingleton<ISpecProvider>(specProvider)
            .AddSingleton<IBlockTree>(blockTree)
            .AddSingleton<IPoSSwitcher>(poSSwitcher)
            .AddSingleton<IRewardCalculatorSource>(new RewardCalculator(specProvider))
            .AddDecorator<IRewardCalculatorSource, MergeRewardCalculatorSource>()
            .Build();

        await container.Resolve<PseudoNethermindRunner>().StartBlockProcessing(default);
        container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        ITraceRpcModule traceRpcModule = container.Resolve<IRpcModuleFactory<ITraceRpcModule>>().Create();

        Block block = Build.A.Block.WithParent(blockTree.Head!).TestObject;
        Assert.That(await blockTree.SuggestBlockAsync(block, BlockTreeSuggestOptions.None), Is.EqualTo(AddBlockResult.Added));

        string response = method == "trace_block"
            ? await RpcTest.TestSerializedRequest(traceRpcModule, method, "0x1")
            : await RpcTest.TestSerializedRequest(traceRpcModule, method, new { fromBlock = "0x1", toBlock = "0x1" });
        JToken? result = JToken.Parse(response)["result"];
        Assert.That(result, Is.InstanceOf<JArray>(), response);
        JArray traces = (JArray)result!;

        if (isPostMerge)
        {
            Assert.That(traces, Is.Empty, response);
            return;
        }

        Assert.That(traces, Has.Count.EqualTo(1), response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(traces[0]!["type"]!.Value<string>(), Is.EqualTo("reward"), response);
            Assert.That(traces[0]!["action"]!["rewardType"]!.Value<string>(), Is.EqualTo("block"), response);
            Assert.That(traces[0]!["action"]!["author"]!.Value<string>(), Is.EqualTo(block.Beneficiary!.ToString()), response);
            Assert.That(traces[0]!["action"]!["value"]!.Value<string>(), Is.EqualTo("0x4563918244f40000"), "5 ETH Frontier block reward");
        }
    }
}
