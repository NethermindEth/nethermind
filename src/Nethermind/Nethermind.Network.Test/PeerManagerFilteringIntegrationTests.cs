// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.All)]
[TestFixture]
public class PeerManagerFilteringIntegrationTests
{
    [Test]
    public void PeerManager_UsesRlpxHostShouldContactInterface()
    {
        CallOrderTrackingMock trackingMock = new();

        ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
        INodeStatsManager stats = new NodeStatsManager(timerFactory, LimboLogs.Instance);
        INetworkStorage storage = new InMemoryStorage();
        INetworkConfig networkConfig = new NetworkConfig
        {
            MaxActivePeers = 25,
            NumConcurrentOutgoingConnects = 1,
            MaxOutgoingConnectPerSec = 1000000
        };

        PeerManager peerManager = new(trackingMock, Substitute.For<IPeerPool>(), stats, networkConfig, LimboLogs.Instance);

        trackingMock.ShouldContact(IPAddress.Parse("203.0.113.1")).Should().BeTrue();
    }

    private class CallOrderTrackingMock : IRlpxHost
    {
        public ConcurrentBag<IPAddress> CallsToShouldContact { get; } = new();
        public ConcurrentBag<Node> CallsToConnectAsync { get; } = new();

        public bool ShouldContact(IPAddress ip)
        {
            CallsToShouldContact.Add(ip);
            return true;
        }

        public Task<bool> ConnectAsync(Node node)
        {
            CallsToConnectAsync.Add(node);
            return Task.FromResult(true);
        }

        public Task Init() => Task.CompletedTask;
        public Task Shutdown() => Task.CompletedTask;
        public PublicKey LocalNodeId { get; } = Core.Test.Builders.TestItem.PublicKeyA;
        public int LocalPort => 30303;
        public event EventHandler<SessionEventArgs>? SessionCreated { add { } remove { } }
        public ISessionMonitor SessionMonitor => Substitute.For<ISessionMonitor>();
    }

    private class InMemoryStorage : INetworkStorage
    {
        private readonly ConcurrentDictionary<PublicKey, NetworkNode> _nodes = new();
        private bool _pendingChanges;

        public NetworkNode[] GetPersistedNodes() => _nodes.Values.ToArray();
        public int PersistedNodesCount => _nodes.Count;

        public void UpdateNode(NetworkNode node)
        {
            _nodes[node.NodeId] = node;
            _pendingChanges = true;
        }

        public void UpdateNodes(IEnumerable<NetworkNode> nodes)
        {
            foreach (NetworkNode node in nodes)
            {
                UpdateNode(node);
            }
        }

        public void RemoveNode(PublicKey nodeId) => _pendingChanges = true;
        public void StartBatch() { }
        public void Commit() { }
        public bool AnyPendingChange() => _pendingChanges;
    }
}
