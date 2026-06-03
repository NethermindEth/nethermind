// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class NodeHealthTrackerTests
{
    private const string Self = "self";
    private const string Remote = "remote";
    private const string Stale = "stale";

    private static (NodeHealthTracker<Hash256, string, Hash256> Tracker, RoutingTableStub Routing, IKademliaMessageSender<Hash256, string> Sender) CreateTracker(
        string? toRefresh = null,
        int failureThreshold = 5,
        TimeSpan? refreshPingTimeout = null,
        IKademliaMessageSender<Hash256, string>? sender = null)
    {
        RoutingTableStub routing = new() { ToRefresh = toRefresh ?? string.Empty };
        sender ??= Substitute.For<IKademliaMessageSender<Hash256, string>>();
        KademliaConfig<string> config = new()
        {
            CurrentNodeId = Self,
            NodeRequestFailureThreshold = failureThreshold,
        };
        if (refreshPingTimeout is { } timeout) config.RefreshPingTimeout = timeout;

        NodeHealthTracker<Hash256, string, Hash256> tracker = new(
            config,
            routing,
            StringNodeHashProvider.Instance,
            sender);
        return (tracker, routing, sender);
    }

    [Test]
    public void OnIncomingMessageFrom_ShouldRefreshSelfWithSelfNode_WhenFullBucketSelectsSelf()
    {
        (NodeHealthTracker<Hash256, string, Hash256> tracker, RoutingTableStub routing, _) = CreateTracker(toRefresh: Self);

        tracker.OnIncomingMessageFrom(Remote);

        Assert.That(routing.AddCalls, Has.Count.EqualTo(2));
        Assert.That(routing.AddCalls[1].Hash, Is.EqualTo(ToHash(ValueKeccak.Compute(Self))));
        Assert.That(routing.AddCalls[1].Node, Is.EqualTo(Self));
    }

    [Test]
    [CancelAfter(10000)]
    public async Task TryRefresh_ShouldRemoveStaleNode_WhenPingTimesOut(CancellationToken token)
    {
        IKademliaMessageSender<Hash256, string> sender = Substitute.For<IKademliaMessageSender<Hash256, string>>();
        sender.Ping(Stale, Arg.Any<CancellationToken>())
            .Returns(false);

        (NodeHealthTracker<Hash256, string, Hash256> tracker, RoutingTableStub routing, _) = CreateTracker(
            toRefresh: Stale,
            sender: sender);

        tracker.OnIncomingMessageFrom(Remote);

        Hash256 staleHash = ToHash(ValueKeccak.Compute(Stale));
        await AssertEventuallyAsync(() => routing.RemoveCalls.Contains(staleHash), token);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task TryRefresh_ShouldKeepNode_WhenPingSucceeds(CancellationToken token)
    {
        IKademliaMessageSender<Hash256, string> sender = Substitute.For<IKademliaMessageSender<Hash256, string>>();
        sender.Ping(Stale, Arg.Any<CancellationToken>()).Returns(true);

        (NodeHealthTracker<Hash256, string, Hash256> tracker, RoutingTableStub routing, _) = CreateTracker(
            toRefresh: Stale,
            sender: sender);

        tracker.OnIncomingMessageFrom(Remote);

        Hash256 staleHash = ToHash(ValueKeccak.Compute(Stale));
        await AssertEventuallyAsync(() => routing.HasAddedNode(staleHash), token);
        Assert.That(routing.RemoveCalls, Does.Not.Contain(staleHash));
    }

    [TestCase(false)]
    [TestCase(true)]
    [CancelAfter(10000)]
    public async Task Dispose_ShouldCancelActiveRefreshWithoutRemovingNode(bool asyncDispose, CancellationToken token)
    {
        TaskCompletionSource pingStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource pingCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IKademliaMessageSender<Hash256, string> sender = Substitute.For<IKademliaMessageSender<Hash256, string>>();
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

        (NodeHealthTracker<Hash256, string, Hash256> tracker, RoutingTableStub routing, _) = CreateTracker(
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
        Assert.That(routing.RemoveCalls, Does.Not.Contain(ToHash(ValueKeccak.Compute(Stale))));
    }

    [Test]
    public void OnRequestFailed_ShouldClearFailureCount_WhenNodeIsRemoved()
    {
        (NodeHealthTracker<Hash256, string, Hash256> tracker, RoutingTableStub routing, _) = CreateTracker(failureThreshold: 1);

        tracker.OnRequestFailed(Remote);
        tracker.OnRequestFailed(Remote);
        tracker.OnRequestFailed(Remote);

        Assert.That(routing.RemoveCalls, Has.Count.EqualTo(1));
        Assert.That(routing.RemoveCalls[0], Is.EqualTo(ToHash(ValueKeccak.Compute(Remote))));
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

    private static Hash256 ToHash(ValueHash256 hash) => hash.ToHash256();

    private sealed class StringNodeHashProvider : INodeHashProvider<string, Hash256>
    {
        public static readonly StringNodeHashProvider Instance = new();

        public Hash256 GetHash(string node) => ToHash(ValueKeccak.Compute(node));
    }

    private sealed class RoutingTableStub : IRoutingTable<string, Hash256>
    {
        public string ToRefresh { get; init; } = string.Empty;

        public List<(Hash256 Hash, string Node)> AddCalls { get; } = [];

        public List<Hash256> RemoveCalls { get; } = [];

        public BucketAddResult TryAddOrRefresh(in Hash256 hash, string item, out string? toRefresh)
        {
            bool isFirstAdd;
            lock (AddCalls)
            {
                AddCalls.Add((hash, item));
                isFirstAdd = AddCalls.Count == 1;
            }

            if (isFirstAdd)
            {
                toRefresh = ToRefresh;
                return BucketAddResult.Full;
            }

            toRefresh = null;
            return BucketAddResult.Refreshed;
        }

        public bool HasAddedNode(Hash256 hash)
        {
            lock (AddCalls)
            {
                foreach ((Hash256 h, string _) in AddCalls)
                {
                    if (h == hash) return true;
                }
            }
            return false;
        }

        public bool Remove(in Hash256 hash)
        {
            lock (RemoveCalls) RemoveCalls.Add(hash);
            return true;
        }

        public string[] GetKNearestNeighbour(Hash256 hash, bool excludeSelf = false) =>
            throw new NotSupportedException();

        public string[] GetKNearestNeighbourExcluding(Hash256 hash, Hash256 exclude, bool excludeSelf = false) =>
            throw new NotSupportedException();

        public string[] GetAllAtDistance(int i) => throw new NotSupportedException();

        public IEnumerable<(Hash256 Prefix, int Distance, KBucket<string, Hash256> Bucket)> IterateBuckets() =>
            throw new NotSupportedException();

        public string? GetByHash(Hash256 nodeId) => throw new NotSupportedException();

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

        public int Size
        {
            get
            {
                lock (AddCalls) return AddCalls.Count;
            }
        }
    }
}
