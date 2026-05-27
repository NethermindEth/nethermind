// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class NodeHealthTrackerTests
{
    private const string Self = "self";
    private const string Remote = "remote";
    private const string Stale = "stale";

    private static (NodeHealthTracker<KademliaHash, string> Tracker, RoutingTableStub Routing, IKademliaMessageSender<KademliaHash, string> Sender) CreateTracker(
        string? toRefresh = null,
        int failureThreshold = 5,
        TimeSpan? refreshPingTimeout = null,
        IKademliaMessageSender<KademliaHash, string>? sender = null)
    {
        RoutingTableStub routing = new() { ToRefresh = toRefresh ?? string.Empty };
        sender ??= Substitute.For<IKademliaMessageSender<KademliaHash, string>>();
        KademliaConfig<string> config = new()
        {
            CurrentNodeId = Self,
            NodeRequestFailureThreshold = failureThreshold,
        };
        if (refreshPingTimeout is { } timeout) config.RefreshPingTimeout = timeout;

        NodeHealthTracker<KademliaHash, string> tracker = new(
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
        (NodeHealthTracker<KademliaHash, string> tracker, RoutingTableStub routing, _) = CreateTracker(toRefresh: Self);

        tracker.OnIncomingMessageFrom(Remote);

        Assert.That(routing.AddCalls, Has.Count.EqualTo(2));
        Assert.That(routing.AddCalls[1].Hash, Is.EqualTo(ToKademliaHash(ValueKeccak.Compute(Self))));
        Assert.That(routing.AddCalls[1].Node, Is.EqualTo(Self));
    }

    [Test]
    [CancelAfter(10000)]
    public async Task TryRefresh_ShouldRemoveStaleNode_WhenPingTimesOut(CancellationToken token)
    {
        IKademliaMessageSender<KademliaHash, string> sender = Substitute.For<IKademliaMessageSender<KademliaHash, string>>();
        sender.Ping(Stale, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new OperationCanceledException()));

        (NodeHealthTracker<KademliaHash, string> tracker, RoutingTableStub routing, _) = CreateTracker(
            toRefresh: Stale,
            refreshPingTimeout: TimeSpan.FromMilliseconds(50),
            sender: sender);

        tracker.OnIncomingMessageFrom(Remote);

        KademliaHash staleHash = ToKademliaHash(ValueKeccak.Compute(Stale));
        await AssertEventuallyAsync(() => routing.RemoveCalls.Contains(staleHash), token);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task TryRefresh_ShouldKeepNode_WhenPingSucceeds(CancellationToken token)
    {
        IKademliaMessageSender<KademliaHash, string> sender = Substitute.For<IKademliaMessageSender<KademliaHash, string>>();
        sender.Ping(Stale, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        (NodeHealthTracker<KademliaHash, string> tracker, RoutingTableStub routing, _) = CreateTracker(
            toRefresh: Stale,
            sender: sender);

        tracker.OnIncomingMessageFrom(Remote);

        KademliaHash staleHash = ToKademliaHash(ValueKeccak.Compute(Stale));
        await AssertEventuallyAsync(() => routing.HasAddedNode(staleHash), token);
        Assert.That(routing.RemoveCalls, Does.Not.Contain(staleHash));
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Dispose_ShouldCancelActiveRefreshWithoutRemovingNode(CancellationToken token)
    {
        TaskCompletionSource pingStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource pingCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IKademliaMessageSender<KademliaHash, string> sender = Substitute.For<IKademliaMessageSender<KademliaHash, string>>();
        sender.Ping(Stale, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            CancellationToken pingToken = call.Arg<CancellationToken>();
            pingStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, pingToken);
            }
            catch (OperationCanceledException)
            {
                pingCancelled.SetResult();
                throw;
            }
        });

        (NodeHealthTracker<KademliaHash, string> tracker, RoutingTableStub routing, _) = CreateTracker(
            toRefresh: Stale,
            refreshPingTimeout: TimeSpan.FromSeconds(10),
            sender: sender);

        tracker.OnIncomingMessageFrom(Remote);
        await pingStarted.Task.WaitAsync(token);

        tracker.Dispose();

        await pingCancelled.Task.WaitAsync(token);
        Assert.That(routing.RemoveCalls, Does.Not.Contain(ToKademliaHash(ValueKeccak.Compute(Stale))));
    }

    [Test]
    public void OnRequestFailed_ShouldClearFailureCount_WhenNodeIsRemoved()
    {
        (NodeHealthTracker<KademliaHash, string> tracker, RoutingTableStub routing, _) = CreateTracker(failureThreshold: 1);

        tracker.OnRequestFailed(Remote);
        tracker.OnRequestFailed(Remote);
        tracker.OnRequestFailed(Remote);

        Assert.That(routing.RemoveCalls, Has.Count.EqualTo(1));
        Assert.That(routing.RemoveCalls[0], Is.EqualTo(ToKademliaHash(ValueKeccak.Compute(Remote))));
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

    private static KademliaHash ToKademliaHash(ValueHash256 hash) => KademliaHash.FromBytes(hash.BytesAsSpan);

    private sealed class StringNodeHashProvider : INodeHashProvider<string>
    {
        public static readonly StringNodeHashProvider Instance = new();

        public KademliaHash GetHash(string node) => ToKademliaHash(ValueKeccak.Compute(node));
    }

    private sealed class RoutingTableStub : IRoutingTable<string>
    {
        public string ToRefresh { get; init; } = string.Empty;

        public List<(KademliaHash Hash, string Node)> AddCalls { get; } = [];

        public List<KademliaHash> RemoveCalls { get; } = [];

        public BucketAddResult TryAddOrRefresh(in KademliaHash hash, string item, out string? toRefresh)
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

        public bool HasAddedNode(KademliaHash hash)
        {
            lock (AddCalls)
            {
                foreach ((KademliaHash h, string _) in AddCalls)
                {
                    if (h == hash) return true;
                }
            }
            return false;
        }

        public bool Remove(in KademliaHash hash)
        {
            lock (RemoveCalls) RemoveCalls.Add(hash);
            return true;
        }

        public string[] GetKNearestNeighbour(KademliaHash hash, KademliaHash? exclude = null, bool excludeSelf = false) =>
            throw new NotSupportedException();

        public string[] GetAllAtDistance(int i) => throw new NotSupportedException();

        public IEnumerable<(KademliaHash Prefix, int Distance, KBucket<string> Bucket)> IterateBuckets() =>
            throw new NotSupportedException();

        public string? GetByHash(KademliaHash nodeId) => throw new NotSupportedException();

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
