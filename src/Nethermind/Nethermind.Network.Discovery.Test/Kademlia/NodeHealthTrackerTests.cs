// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Kademlia;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class NodeHealthTrackerTests
{
    private const int Self = 0;
    private const int Remote = 1;
    private const int Stale = 2;

    private static (NodeHealthTracker<int, int, int> Tracker, RoutingTableStub Routing, IKademliaMessageSender<int, int> Sender) CreateTracker(
        int? toRefresh = null,
        int failureThreshold = 5,
        TimeSpan? refreshPingTimeout = null,
        IKademliaMessageSender<int, int>? sender = null,
        Func<int, bool>? canRefreshNode = null)
    {
        RoutingTableStub routing = new() { ToRefresh = toRefresh };
        sender ??= Substitute.For<IKademliaMessageSender<int, int>>();
        KademliaConfig<int> config = new()
        {
            CurrentNodeId = Self,
            NodeRequestFailureThreshold = failureThreshold,
            CanRefreshNode = canRefreshNode,
        };
        if (refreshPingTimeout is { } timeout) config.RefreshPingTimeout = timeout;

        NodeHealthTracker<int, int, int> tracker = new(
            config,
            routing,
            IntNodeHashProvider.Instance,
            sender);
        return (tracker, routing, sender);
    }

    [Test]
    public void OnIncomingMessageFrom_ShouldRefreshSelfWithSelfNode_WhenFullBucketSelectsSelf()
    {
        (NodeHealthTracker<int, int, int> tracker, RoutingTableStub routing, _) = CreateTracker(toRefresh: Self);

        tracker.OnIncomingMessageFrom(Remote);

        Assert.That(routing.AddCalls, Has.Count.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(routing.AddCalls[1].Hash, Is.EqualTo(Self));
            Assert.That(routing.AddCalls[1].Node, Is.EqualTo(Self));
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task TryRefresh_ShouldRemoveStaleNode_WhenPingTimesOut(CancellationToken token)
    {
        IKademliaMessageSender<int, int> sender = Substitute.For<IKademliaMessageSender<int, int>>();
        sender.Ping(Stale, Arg.Any<CancellationToken>())
            .Returns(false);

        (NodeHealthTracker<int, int, int> tracker, RoutingTableStub routing, _) = CreateTracker(
            toRefresh: Stale,
            sender: sender);

        tracker.OnIncomingMessageFrom(Remote);

        await AsyncTestHelpers.AssertEventuallyAsync(() => routing.RemoveCalls.Contains(Stale), token);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task TryRefresh_ShouldClearFailureCount_WhenNodeIsEvicted(CancellationToken token)
    {
        IKademliaMessageSender<int, int> sender = Substitute.For<IKademliaMessageSender<int, int>>();
        sender.Ping(Stale, Arg.Any<CancellationToken>()).Returns(false);

        (NodeHealthTracker<int, int, int> tracker, RoutingTableStub routing, _) = CreateTracker(
            toRefresh: Stale,
            failureThreshold: 2,
            sender: sender);

        tracker.OnRequestFailed(Stale);
        tracker.OnIncomingMessageFrom(Remote);
        await AsyncTestHelpers.AssertEventuallyAsync(() => routing.RemoveCalls.Contains(Stale), token);

        tracker.OnRequestFailed(Stale);
        tracker.OnRequestFailed(Stale);
        Assert.That(routing.RemoveCalls, Has.Count.EqualTo(1));

        tracker.OnRequestFailed(Stale);
        Assert.That(routing.RemoveCalls, Has.Count.EqualTo(2));
    }

    [Test]
    [CancelAfter(10000)]
    public async Task TryRefresh_ShouldKeepNode_WhenPingSucceeds(CancellationToken token)
    {
        IKademliaMessageSender<int, int> sender = Substitute.For<IKademliaMessageSender<int, int>>();
        sender.Ping(Stale, Arg.Any<CancellationToken>()).Returns(true);

        (NodeHealthTracker<int, int, int> tracker, RoutingTableStub routing, _) = CreateTracker(
            toRefresh: Stale,
            sender: sender);

        tracker.OnIncomingMessageFrom(Remote);

        await AsyncTestHelpers.AssertEventuallyAsync(() => routing.HasAddedNode(Stale), token);
        Assert.That(routing.RemoveCalls, Does.Not.Contain(Stale));
    }

    [Test]
    [CancelAfter(10000)]
    public async Task TryRefresh_ShouldRemoveNode_WhenRefreshIsNotAdmitted(CancellationToken token)
    {
        IKademliaMessageSender<int, int> sender = Substitute.For<IKademliaMessageSender<int, int>>();
        sender.Ping(Stale, Arg.Any<CancellationToken>()).Returns(true);

        (NodeHealthTracker<int, int, int> tracker, RoutingTableStub routing, _) = CreateTracker(
            toRefresh: Stale,
            sender: sender,
            canRefreshNode: _ => false);

        tracker.OnIncomingMessageFrom(Remote);

        await AsyncTestHelpers.AssertEventuallyAsync(() => routing.RemoveCalls.Contains(Stale), token);
        Assert.That(routing.HasAddedNode(Stale), Is.False);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task TryRefresh_ShouldKeepNode_WhenRefreshIsAdmitted(CancellationToken token)
    {
        IKademliaMessageSender<int, int> sender = Substitute.For<IKademliaMessageSender<int, int>>();
        sender.Ping(Stale, Arg.Any<CancellationToken>()).Returns(true);

        (NodeHealthTracker<int, int, int> tracker, RoutingTableStub routing, _) = CreateTracker(
            toRefresh: Stale,
            sender: sender,
            canRefreshNode: _ => true);

        tracker.OnIncomingMessageFrom(Remote);

        await AsyncTestHelpers.AssertEventuallyAsync(() => routing.HasAddedNode(Stale), token);
        Assert.That(routing.RemoveCalls, Does.Not.Contain(Stale));
    }

    [Test]
    [CancelAfter(10000)]
    public async Task TryRefresh_ShouldUseAdmissionAssignedAfterConstruction(CancellationToken token)
    {
        RoutingTableStub routing = new() { ToRefresh = Stale };
        IKademliaMessageSender<int, int> sender = Substitute.For<IKademliaMessageSender<int, int>>();
        sender.Ping(Stale, Arg.Any<CancellationToken>()).Returns(true);
        KademliaConfig<int> config = new() { CurrentNodeId = Self };
        NodeHealthTracker<int, int, int> tracker = new(config, routing, IntNodeHashProvider.Instance, sender);
        config.CanRefreshNode = _ => false;

        tracker.OnIncomingMessageFrom(Remote);

        await AsyncTestHelpers.AssertEventuallyAsync(() => routing.RemoveCalls.Contains(Stale), token);
    }

    [TestCase(false)]
    [TestCase(true)]
    [CancelAfter(10000)]
    public async Task Dispose_ShouldCancelActiveRefreshWithoutRemovingNode(bool asyncDispose, CancellationToken token)
    {
        TaskCompletionSource pingStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource pingCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IKademliaMessageSender<int, int> sender = Substitute.For<IKademliaMessageSender<int, int>>();
        sender.Ping(Stale, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            CancellationToken pingToken = call.Arg<CancellationToken>();
            pingStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, pingToken);
                return true;
            }
            catch (OperationCanceledException)
            {
                pingCancelled.SetResult();
                throw;
            }
        });

        (NodeHealthTracker<int, int, int> tracker, RoutingTableStub routing, _) = CreateTracker(
            toRefresh: Stale,
            refreshPingTimeout: TimeSpan.FromSeconds(10),
            sender: sender);

        tracker.OnIncomingMessageFrom(Remote);
        await pingStarted.Task.WaitAsync(token);

        if (asyncDispose)
        {
            await tracker.DisposeAsync();
        }
        else
        {
            tracker.Dispose();
        }

        await pingCancelled.Task.WaitAsync(token);
        Assert.That(routing.RemoveCalls, Does.Not.Contain(Stale));
    }

    [Test]
    public void OnRequestFailed_ShouldClearFailureCount_WhenNodeIsRemoved()
    {
        (NodeHealthTracker<int, int, int> tracker, RoutingTableStub routing, _) = CreateTracker(failureThreshold: 1);

        tracker.OnRequestFailed(Remote);
        tracker.OnRequestFailed(Remote);
        tracker.OnRequestFailed(Remote);

        Assert.That(routing.RemoveCalls, Has.Count.EqualTo(1));
        Assert.That(routing.RemoveCalls[0], Is.EqualTo(Remote));
    }

    private sealed class RoutingTableStub : IRoutingTable<int, int>
    {
        public int? ToRefresh { get; init; }

        public List<(int Hash, int Node)> AddCalls { get; } = [];

        public List<int> RemoveCalls { get; } = [];

        public BucketAddResult TryAddOrRefresh(in int hash, int item, out int toRefresh)
        {
            bool isFirstAdd;
            lock (AddCalls)
            {
                AddCalls.Add((hash, item));
                isFirstAdd = AddCalls.Count == 1;
            }

            if (isFirstAdd && ToRefresh is not null)
            {
                toRefresh = ToRefresh.Value;
                return BucketAddResult.Full;
            }

            toRefresh = default;
            return BucketAddResult.Refreshed;
        }

        public bool HasAddedNode(int hash)
        {
            lock (AddCalls)
            {
                foreach ((int h, int _) in AddCalls)
                {
                    if (h == hash) return true;
                }
            }
            return false;
        }

        public bool Remove(in int hash)
        {
            lock (RemoveCalls) RemoveCalls.Add(hash);
            return true;
        }

        public int[] GetKNearestNeighbour(int hash, bool excludeSelf = false) =>
            throw new NotSupportedException();

        public int[] GetKNearestNeighbourExcluding(int hash, int exclude, bool excludeSelf = false) =>
            throw new NotSupportedException();

        public int[] GetAllAtDistance(int i) => throw new NotSupportedException();

        public IEnumerable<RoutingTableBucket<int, int>> IterateBuckets() =>
            throw new NotSupportedException();

        public bool TryGet(in int hash, out int node) => throw new NotSupportedException();

        public void LogDebugInfo() => throw new NotSupportedException();

        public event EventHandler<int>? OnNodeAdded
        {
            add { }
            remove { }
        }

        public event EventHandler<int>? OnNodeRemoved
        {
            add { }
            remove { }
        }

        public RoutingTableOccupancy GetOccupancy() => throw new NotSupportedException();
    }
}
