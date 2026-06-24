// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Kademlia;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class RandomWalkKademliaDiscoveryTests
{
    [Test]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_should_stream_nodes_from_random_lookup(CancellationToken token)
    {
        TestKademlia kademlia = new();
        RandomWalkKademliaDiscovery<int, int, int> discovery = new(
            kademlia,
            IntKeyOperator.Instance,
            Int32KademliaDistance.Instance,
            new KademliaConfig<int> { CurrentNodeId = 0 },
            LimboLogs.Instance);

        List<int> nodes = await discovery.DiscoverNodes(1, 2, token).Take(2).ToListAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodes, Is.EqualTo(new[] { 1, 2 }));
            Assert.That(kademlia.LookupNodesCalls, Is.EqualTo(1));
            Assert.That(kademlia.LastMaxResults, Is.EqualTo(2));
        }
    }

    private sealed class TestKademlia : IKademlia<int, int>
    {
        public event EventHandler<int>? OnNodeAdded { add { } remove { } }
        public event EventHandler<int>? OnNodeRemoved { add { } remove { } }

        public int LookupNodesCalls { get; private set; }
        public int? LastMaxResults { get; private set; }

        public void AddOrRefresh(int node) => throw new NotSupportedException();

        public void Remove(int node) => throw new NotSupportedException();

        public Task Run(CancellationToken token) => throw new NotSupportedException();

        public Task Bootstrap(CancellationToken token) => throw new NotSupportedException();

        public Task<int[]> LookupNodesClosest(int key, CancellationToken token, int? k = null) => throw new NotSupportedException();

        public IAsyncEnumerable<int> LookupNodes(int key, CancellationToken token, int? maxResults = null)
        {
            LookupNodesCalls++;
            LastMaxResults = maxResults;
            return CreateAsyncEnumerable(1, 2);
        }

        public int[] GetKNeighbour(int target, int excluding = 0, bool excludeSelf = false) => throw new NotSupportedException();

        public int[] GetAllAtDistance(int distance) => throw new NotSupportedException();

        public IEnumerable<int> IterateNodes() => throw new NotSupportedException();
    }

    private sealed class IntKeyOperator : IKeyOperator<int, int, int>
    {
        public static IntKeyOperator Instance { get; } = new();

        public int GetKey(int node) => node;

        public int GetKeyHash(int key) => key;

        public int CreateRandomKeyAtDistance(int nodePrefix, int depth) => depth;
    }

    private static async IAsyncEnumerable<T> CreateAsyncEnumerable<T>(params IEnumerable<T> items)
    {
        foreach (T item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }
}
