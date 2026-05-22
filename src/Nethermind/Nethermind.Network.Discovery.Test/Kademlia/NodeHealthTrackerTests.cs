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
    [Test]
    public void OnIncomingMessageFrom_ShouldRefreshSelfWithSelfNode_WhenFullBucketSelectsSelf()
    {
        const string self = "self";
        const string remote = "remote";
        RoutingTableStub routingTable = new() { ToRefresh = self };
        NodeHealthTracker<ValueHash256, string> tracker = new(
            new KademliaConfig<string> { CurrentNodeId = self },
            routingTable,
            new StringNodeHashProvider(),
            Substitute.For<IKademliaMessageSender<ValueHash256, string>>(),
            LimboLogs.Instance);

        tracker.OnIncomingMessageFrom(remote);

        Assert.That(routingTable.AddCalls, Has.Count.EqualTo(2));
        Assert.That(routingTable.AddCalls[1].Hash, Is.EqualTo(ValueKeccak.Compute(self)));
        Assert.That(routingTable.AddCalls[1].Node, Is.EqualTo(self));
    }

    [Test]
    [CancelAfter(10000)]
    public async Task TryRefresh_ShouldRemoveStaleNode_WhenPingTimesOut(CancellationToken token)
    {
        const string self = "self";
        const string remote = "remote";
        const string stale = "stale";
        RoutingTableStub routingTable = new() { ToRefresh = stale };
        IKademliaMessageSender<ValueHash256, string> sender = Substitute.For<IKademliaMessageSender<ValueHash256, string>>();
        sender.Ping(stale, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new OperationCanceledException()));

        NodeHealthTracker<ValueHash256, string> tracker = new(
            new KademliaConfig<string>
            {
                CurrentNodeId = self,
                RefreshPingTimeout = TimeSpan.FromMilliseconds(50),
            },
            routingTable,
            new StringNodeHashProvider(),
            sender,
            LimboLogs.Instance);

        tracker.OnIncomingMessageFrom(remote);

        ValueHash256 staleHash = ValueKeccak.Compute(stale);
        await AssertEventuallyAsync(() => routingTable.RemoveCalls.Contains(staleHash), token);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task TryRefresh_ShouldKeepNode_WhenPingSucceeds(CancellationToken token)
    {
        const string self = "self";
        const string remote = "remote";
        const string stale = "stale";
        RoutingTableStub routingTable = new() { ToRefresh = stale };
        IKademliaMessageSender<ValueHash256, string> sender = Substitute.For<IKademliaMessageSender<ValueHash256, string>>();
        sender.Ping(stale, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        NodeHealthTracker<ValueHash256, string> tracker = new(
            new KademliaConfig<string> { CurrentNodeId = self },
            routingTable,
            new StringNodeHashProvider(),
            sender,
            LimboLogs.Instance);

        tracker.OnIncomingMessageFrom(remote);

        ValueHash256 staleHash = ValueKeccak.Compute(stale);
        // OnIncomingMessageFrom inside TryRefresh's success branch re-adds the stale node — wait for that.
        await AssertEventuallyAsync(() => routingTable.HasAddedNode(staleHash), token);
        Assert.That(routingTable.RemoveCalls, Does.Not.Contain(staleHash));
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

    [Test]
    public void OnRequestFailed_ShouldClearFailureCount_WhenNodeIsRemoved()
    {
        const string self = "self";
        const string remote = "remote";
        RoutingTableStub routingTable = new();
        NodeHealthTracker<ValueHash256, string> tracker = new(
            new KademliaConfig<string> { CurrentNodeId = self, NodeRequestFailureThreshold = 1 },
            routingTable,
            new StringNodeHashProvider(),
            Substitute.For<IKademliaMessageSender<ValueHash256, string>>(),
            LimboLogs.Instance);

        tracker.OnRequestFailed(remote);
        tracker.OnRequestFailed(remote);
        tracker.OnRequestFailed(remote);

        Assert.That(routingTable.RemoveCalls, Has.Count.EqualTo(1));
        Assert.That(routingTable.RemoveCalls[0], Is.EqualTo(ValueKeccak.Compute(remote)));
    }

    private sealed class StringNodeHashProvider : INodeHashProvider<string>
    {
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
