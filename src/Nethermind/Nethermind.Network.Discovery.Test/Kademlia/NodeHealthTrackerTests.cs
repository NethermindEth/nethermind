// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class NodeHealthTrackerTests
{
    private const string Self = "self";
    private const string Remote = "remote";
    private const string Stale = "stale";

    private static (NodeHealthTracker<ValueHash256, string> Tracker, RoutingTableStub Routing, IKademliaMessageSender<ValueHash256, string> Sender) CreateTracker(
        string? toRefresh = null,
        int failureThreshold = 5,
        IKademliaMessageSender<ValueHash256, string>? sender = null)
    {
        RoutingTableStub routing = new() { ToRefresh = toRefresh ?? string.Empty };
        sender ??= Substitute.For<IKademliaMessageSender<ValueHash256, string>>();
        KademliaConfig<string> config = new()
        {
            CurrentNodeId = Self,
            NodeRequestFailureThreshold = failureThreshold,
        };

        NodeHealthTracker<ValueHash256, string> tracker = new(
            config,
            routing,
            StringNodeHashProvider.Instance,
            sender,
            LimboLogs.Instance);
        return (tracker, routing, sender);
    }

    [Test]
    public void OnIncomingMessageFrom_ShouldRefreshSelfWithSelfNode_WhenFullBucketSelectsSelf()
    {
        (NodeHealthTracker<ValueHash256, string> tracker, RoutingTableStub routing, _) = CreateTracker(toRefresh: Self);

        tracker.OnIncomingMessageFrom(Remote);

        Assert.That(routing.AddCalls, Has.Count.EqualTo(2));
        Assert.That(routing.AddCalls[1].Hash, Is.EqualTo(ValueKeccak.Compute(Self)));
        Assert.That(routing.AddCalls[1].Node, Is.EqualTo(Self));
    }

    [Test]
    [CancelAfter(10000)]
    public async Task TryRefresh_ShouldRemoveStaleNode_WhenPingTimesOut(CancellationToken token)
    {
        IKademliaMessageSender<ValueHash256, string> sender = Substitute.For<IKademliaMessageSender<ValueHash256, string>>();
        sender.Ping(Stale, Arg.Any<CancellationToken>())
            .Returns(false);

        (NodeHealthTracker<ValueHash256, string> tracker, RoutingTableStub routing, _) = CreateTracker(
            toRefresh: Stale,
            sender: sender);

        tracker.OnIncomingMessageFrom(Remote);

        await AssertEventuallyAsync(() => routing.RemoveCalls.Contains(ValueKeccak.Compute(Stale)), token);
        await sender.Received(1).Ping(Stale, Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
    }

    [Test]
    [CancelAfter(10000)]
    public async Task TryRefresh_ShouldKeepNode_WhenPingSucceeds(CancellationToken token)
    {
        IKademliaMessageSender<ValueHash256, string> sender = Substitute.For<IKademliaMessageSender<ValueHash256, string>>();
        sender.Ping(Stale, Arg.Any<CancellationToken>()).Returns(true);

        (NodeHealthTracker<ValueHash256, string> tracker, RoutingTableStub routing, _) = CreateTracker(
            toRefresh: Stale,
            sender: sender);

        tracker.OnIncomingMessageFrom(Remote);

        ValueHash256 staleHash = ValueKeccak.Compute(Stale);
        await AssertEventuallyAsync(() => routing.HasAddedNode(staleHash), token);
        Assert.That(routing.RemoveCalls, Does.Not.Contain(staleHash));
    }

    [Test]
    public void OnRequestFailed_ShouldClearFailureCount_WhenNodeIsRemoved()
    {
        (NodeHealthTracker<ValueHash256, string> tracker, RoutingTableStub routing, _) = CreateTracker(failureThreshold: 1);

        tracker.OnRequestFailed(Remote);
        tracker.OnRequestFailed(Remote);
        tracker.OnRequestFailed(Remote);

        Assert.That(routing.RemoveCalls, Has.Count.EqualTo(1));
        Assert.That(routing.RemoveCalls[0], Is.EqualTo(ValueKeccak.Compute(Remote)));
    }

    private static async Task AssertEventuallyAsync(Func<bool> condition, CancellationToken token)
    {
        for (int i = 0; i < 50; i++)
        {
            if (condition()) return;
            await Task.Delay(50, token);
        }
        Assert.Fail("Condition not met within timeout.");
    }

    private sealed class StringNodeHashProvider : INodeHashProvider<string>
    {
        public static readonly StringNodeHashProvider Instance = new();
        public ValueHash256 GetHash(string node) => ValueKeccak.Compute(node);
    }

    private sealed class RoutingTableStub : IRoutingTable<string>
    {
        public string ToRefresh { get; init; } = string.Empty;

        public List<(ValueHash256 Hash, string Node)> AddCalls { get; } = [];

        public List<ValueHash256> RemoveCalls { get; } = [];

        public BucketAddResult TryAddOrRefresh(in ValueHash256 hash, string item, out string? toRefresh)
        {
            lock (AddCalls) AddCalls.Add((hash, item));
            if (AddCalls.Count == 1)
            {
                toRefresh = ToRefresh;
                return BucketAddResult.Full;
            }

            toRefresh = null;
            return BucketAddResult.Refreshed;
        }

        public bool HasAddedNode(ValueHash256 hash)
        {
            lock (AddCalls)
            {
                foreach ((ValueHash256 h, string _) in AddCalls)
                {
                    if (h == hash) return true;
                }
            }
            return false;
        }

        public bool Remove(in ValueHash256 hash)
        {
            lock (RemoveCalls) RemoveCalls.Add(hash);
            return true;
        }

        public string[] GetKNearestNeighbour(ValueHash256 hash, ValueHash256? exclude = null, bool excludeSelf = false) =>
            throw new NotSupportedException();

        public string[] GetAllAtDistance(int i) => throw new NotSupportedException();

        public IEnumerable<(ValueHash256 Prefix, int Distance, KBucket<string> Bucket)> IterateBuckets() =>
            throw new NotSupportedException();

        public string? GetByHash(ValueHash256 nodeId) => throw new NotSupportedException();

        public void LogDebugInfo() => throw new NotSupportedException();

        public event EventHandler<string>? OnNodeAdded
        {
            add { }
            remove { }
        }

        public event EventHandler<string>? OnNodeRemoved
        {
            add { }
            remove { }
        }

        public int Size => AddCalls.Count;
    }
}
