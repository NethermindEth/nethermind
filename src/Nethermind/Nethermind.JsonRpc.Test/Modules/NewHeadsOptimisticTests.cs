// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

[Parallelizable(ParallelScope.All)]
public class NewHeadsOptimisticTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [TestCase("true", true, null, null)]
    [TestCase("false", false, null, null)]
    [TestCase("{\"includeTransactions\":true}", true, null, null)]
    [TestCase("{\"headerOnly\":true}", false, true, null)]
    [TestCase("{\"optimistic\":true,\"headerOnly\":false}", false, false, true)]
    [TestCase("{\"includeTransactions\":false,\"optimistic\":false,\"headerOnly\":null}", false, null, false)]
    public void Option_reads_its_flags(string json, bool includeTransactions, bool? headerOnly, bool? optimistic)
    {
        NewHeadsOption option = Read(json);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(option.IncludeTransactions, Is.EqualTo(includeTransactions));
            Assert.That(option.HeaderOnly, Is.EqualTo(headerOnly));
            Assert.That(option.Optimistic, Is.EqualTo(optimistic));
        }
    }

    [Test]
    public void Option_rejects_a_flag_that_is_not_a_boolean() =>
        Assert.Throws<JsonException>(() => Read("{\"optimistic\":\"yes\"}"));

    [Test]
    public void Header_only_announcement_carries_the_header_alone()
    {
        Block block = Build.A.Block.WithNumber(1).WithTransactions(3, MainnetSpecProvider.Instance).WithWithdrawals(2).TestObject;
        Harness harness = new();
        using NewHeadSubscription subscription = harness.Subscribe(headerOnly: true);

        harness.BlockTree.BlockAddedToMain += Raise.EventWith(new object(), new BlockReplacementEventArgs(block));

        JsonElement result = harness.Next();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("hash").GetString(), Is.EqualTo(block.Hash!.ToString()));
            Assert.That(result.GetProperty("number").GetString(), Is.EqualTo("0x1"));
            Assert.That(result.TryGetProperty("parentHash", out _), Is.True);
            Assert.That(result.TryGetProperty("withdrawalsRoot", out _), Is.True);
            Assert.That(result.TryGetProperty("transactions", out _), Is.False);
            Assert.That(result.TryGetProperty("withdrawals", out _), Is.False);
            Assert.That(result.TryGetProperty("uncles", out _), Is.False);
            Assert.That(result.TryGetProperty("size", out _), Is.False);
        }
    }

    [Test]
    public void Include_transactions_wins_over_header_only()
    {
        Block block = Build.A.Block.WithNumber(1).WithTransactions(2, MainnetSpecProvider.Instance).TestObject;
        Harness harness = new();
        using NewHeadSubscription subscription = harness.Subscribe(includeTransactions: true, headerOnly: true);

        harness.BlockTree.BlockAddedToMain += Raise.EventWith(new object(), new BlockReplacementEventArgs(block));

        JsonElement transactions = harness.Next().GetProperty("transactions");
        Assert.That(transactions.GetArrayLength(), Is.EqualTo(2));
        Assert.That(transactions[0].ValueKind, Is.EqualTo(JsonValueKind.Object));
    }

    [Test]
    public void Optimistic_head_is_announced_once_and_a_different_winner_as_a_reorg()
    {
        Block parent = Build.A.Block.WithNumber(10).TestObject;
        Block optimistic = Build.A.Block.WithNumber(11).WithParentHash(parent.Hash!).WithExtraData([1]).TestObject;
        Block winner = Build.A.Block.WithNumber(11).WithParentHash(parent.Hash!).WithExtraData([2]).TestObject;
        Harness harness = new();
        TestOptimisticHeads heads = new();
        using NewHeadSubscription subscription = harness.Subscribe(optimisticHeads: heads);

        heads.Raise(optimistic);
        harness.BlockTree.BlockAddedToMain += Raise.EventWith(new object(), new BlockReplacementEventArgs(optimistic));
        harness.BlockTree.BlockAddedToMain += Raise.EventWith(new object(), new BlockReplacementEventArgs(winner));
        harness.BlockTree.BlockAddedToMain += Raise.EventWith(new object(), new BlockReplacementEventArgs(optimistic));

        string?[] announced = [Hash(harness.Next()), Hash(harness.Next()), Hash(harness.Next())];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(announced, Is.EqualTo(new[] { optimistic.Hash!.ToString(), winner.Hash!.ToString(), optimistic.Hash!.ToString() }));
            Assert.That(harness.NoMore(), Is.True, "the canonical copy of the optimistic head must not be announced again");
        }
    }

    [Test]
    public void Subscription_without_optimistic_heads_ignores_them_and_repeats_canonical_heads()
    {
        Block block = Build.A.Block.WithNumber(5).TestObject;
        Harness harness = new();
        TestOptimisticHeads heads = new();
        using NewHeadSubscription subscription = harness.Subscribe();

        heads.Raise(block);
        harness.BlockTree.BlockAddedToMain += Raise.EventWith(new object(), new BlockReplacementEventArgs(block));
        harness.BlockTree.BlockAddedToMain += Raise.EventWith(new object(), new BlockReplacementEventArgs(block));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Hash(harness.Next()), Is.EqualTo(block.Hash!.ToString()));
            Assert.That(Hash(harness.Next()), Is.EqualTo(block.Hash!.ToString()), "without optimistic mode every canonical event is announced, as before");
            Assert.That(heads.HasSubscribers, Is.False);
        }
    }

    [TestCase(null, false, false)]
    [TestCase(null, true, true)]
    [TestCase("{\"optimistic\":true}", false, true)]
    [TestCase("{\"optimistic\":false}", true, false)]
    public void Factory_takes_the_node_default_unless_the_subscription_says_otherwise(string? args, bool nodeDefault, bool expected)
    {
        TestOptimisticHeads heads = new();
        SubscriptionFactory factory = new();
        factory.RegisterNewHeadSubscription(
            Substitute.For<IBlockTree>(), LimboLogs.Instance, MainnetSpecProvider.Instance, new BlockForRpcFactory(),
            new JsonRpcConfig { NewHeadsOptimistic = nodeDefault }, heads);

        using Subscription subscription = factory.CreateSubscription(Substitute.For<IJsonRpcDuplexClient>(), SubscriptionType.EthSubscription.NewHeads, args);

        Assert.That(heads.HasSubscribers, Is.EqualTo(expected));
    }

    [Test]
    public void Factory_header_only_default_applies_to_subscriptions_without_the_parameter()
    {
        Block block = Build.A.Block.WithNumber(1).WithTransactions(1, MainnetSpecProvider.Instance).TestObject;
        Harness harness = new();
        SubscriptionFactory factory = new();
        factory.RegisterNewHeadSubscription(harness.BlockTree, LimboLogs.Instance, MainnetSpecProvider.Instance, new BlockForRpcFactory(),
            new JsonRpcConfig { NewHeadsHeaderOnly = true });

        using Subscription headerOnly = factory.CreateSubscription(harness.Client, SubscriptionType.EthSubscription.NewHeads);
        harness.BlockTree.BlockAddedToMain += Raise.EventWith(new object(), new BlockReplacementEventArgs(block));

        Assert.That(harness.Next().TryGetProperty("transactions", out _), Is.False);
    }

    [TestCase(ProcessingResult.Success, 11, true, 0, true, TestName = "next block on the head")]
    [TestCase(ProcessingResult.Exception, 11, true, 0, false, TestName = "failed processing")]
    [TestCase(ProcessingResult.Success, 12, true, 0, false, TestName = "not the next number")]
    [TestCase(ProcessingResult.Success, 11, false, 0, false, TestName = "side branch")]
    [TestCase(ProcessingResult.Success, 11, true, 25, false, TestName = "older than two slots")]
    public void Source_raises_only_for_a_fresh_block_on_top_of_the_head(ProcessingResult result, int number, bool onHead, int ageSeconds, bool raised)
    {
        const ulong now = 2_000_000;
        Block head = Build.A.Block.WithNumber(10).WithTimestamp(now - 12).TestObject;
        Block block = Build.A.Block.WithNumber(number)
            .WithParentHash(onHead ? head.Hash! : Keccak.Compute("other"))
            .WithTimestamp(now - (ulong)ageSeconds)
            .TestObject;
        IBlockProcessingQueue queue = Substitute.For<IBlockProcessingQueue>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(head);
        blockTree.FindBlock(block.Hash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(block);
        using OptimisticHeadSource source = new(queue, blockTree, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(now)), 12, LimboLogs.Instance);
        List<Block> heads = [];
        source.OptimisticHead += (_, e) => heads.Add(e.Block);

        queue.BlockRemoved += Raise.EventWith(queue, new BlockRemovedEventArgs(block.Hash!, result));

        if (raised) Assert.That(heads, Is.EqualTo(new[] { block }));
        else Assert.That(heads, Is.Empty);
    }

    [Test]
    public void Source_does_no_lookup_without_subscribers()
    {
        IBlockProcessingQueue queue = Substitute.For<IBlockProcessingQueue>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        using OptimisticHeadSource source = new(queue, blockTree, new ManualTimestamper(DateTime.UtcNow), 12, LimboLogs.Instance);

        queue.BlockRemoved += Raise.EventWith(queue, new BlockRemovedEventArgs(Keccak.Compute("block"), ProcessingResult.Success));

        blockTree.DidNotReceiveWithAnyArgs().FindBlock(default(Hash256)!, default(BlockTreeLookupOptions), default);
    }

    [Test]
    public void Source_keeps_a_failing_subscriber_away_from_the_processing_loop()
    {
        Block head = Build.A.Block.WithNumber(10).TestObject;
        Block block = Build.A.Block.WithNumber(11).WithParentHash(head.Hash!).WithTimestamp(head.Timestamp + 12).TestObject;
        IBlockProcessingQueue queue = Substitute.For<IBlockProcessingQueue>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(head);
        blockTree.FindBlock(block.Hash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(block);
        using OptimisticHeadSource source = new(queue, blockTree, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(block.Timestamp)), 12, LimboLogs.Instance);
        source.OptimisticHead += (_, _) => throw new InvalidOperationException("subscriber failed");

        Assert.DoesNotThrow(() => queue.BlockRemoved += Raise.EventWith(queue, new BlockRemovedEventArgs(block.Hash!, ProcessingResult.Success)));
    }

    private static NewHeadsOption Read(string json)
    {
        NewHeadsOption option = new();
        using JsonDocument document = JsonDocument.Parse(json);
        option.ReadJson(document.RootElement, EthereumJsonSerializer.JsonOptions);
        return option;
    }

    private static string? Hash(JsonElement result) => result.GetProperty("hash").GetString();

    /// <summary>A substitute client that records every notification's result as parsed JSON.</summary>
    private sealed class Harness
    {
        private readonly BlockingCollection<JsonElement> _results = new();

        public IBlockTree BlockTree { get; } = Substitute.For<IBlockTree>();
        public IJsonRpcDuplexClient Client { get; } = Substitute.For<IJsonRpcDuplexClient>();

        public Harness() =>
            Client.SendJsonRpcResult(Arg.Do<JsonRpcResult>(r =>
            {
                using JsonDocument document = JsonDocument.Parse(RpcTest.SerializeResponse(r.Response!));
                _results.Add(document.RootElement.GetProperty("params").GetProperty("result").Clone());
            }));

        public NewHeadSubscription Subscribe(bool includeTransactions = false, bool headerOnly = false, IOptimisticHeadSource? optimisticHeads = null) =>
            new(Client, BlockTree, LimboLogs.Instance, new NewHeadPayloadCache(MainnetSpecProvider.Instance, new BlockForRpcFactory()),
                includeTransactions, headerOnly, optimisticHeads);

        public JsonElement Next() =>
            _results.TryTake(out JsonElement result, Wait) ? result : throw new TimeoutException("no notification");

        public bool NoMore() => !_results.TryTake(out _, TimeSpan.FromMilliseconds(200));
    }

    private sealed class TestOptimisticHeads : IOptimisticHeadSource
    {
        public event EventHandler<BlockEventArgs>? OptimisticHead;

        public bool HasSubscribers => OptimisticHead is not null;

        public void Raise(Block block) => OptimisticHead?.Invoke(this, new BlockEventArgs(block));
    }
}
