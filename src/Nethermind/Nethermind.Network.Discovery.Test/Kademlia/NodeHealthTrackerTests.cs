// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
            AddCalls.Add((hash, item));
            if (AddCalls.Count == 1)
            {
                toRefresh = ToRefresh;
                return BucketAddResult.Full;
            }

            toRefresh = null;
            return BucketAddResult.Refreshed;
        }

        public bool Remove(in ValueHash256 hash)
        {
            RemoveCalls.Add(hash);
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
