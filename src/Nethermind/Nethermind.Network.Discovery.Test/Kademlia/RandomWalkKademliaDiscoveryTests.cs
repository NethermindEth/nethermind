// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Kademlia;
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
            new KademliaConfig<int> { CurrentNodeId = 0 });

        List<int> nodes = await discovery.DiscoverNodes(1, 2, token).Take(2).ToListAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodes, Is.EqualTo(new[] { 1, 2 }));
            Assert.That(kademlia.LookupNodesCalls, Is.EqualTo(1));
            Assert.That(kademlia.LastMaxResults, Is.EqualTo(2));
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_should_pace_iterations_to_minimum_iteration_duration(CancellationToken token)
    {
        TestKademlia kademlia = new();
        RandomWalkKademliaDiscovery<int, int, int> discovery = new(
            kademlia,
            IntKeyOperator.Instance,
            Int32KademliaDistance.Instance,
            new KademliaConfig<int> { CurrentNodeId = 0 });

        Stopwatch stopwatch = Stopwatch.StartNew();
        List<int> nodes = await discovery.DiscoverNodes(1, 2, token).Take(4).ToListAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodes, Is.EqualTo(new[] { 1, 2, 1, 2 }));
            Assert.That(stopwatch.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(950)));
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_should_not_delay_when_lookup_exceeds_minimum_iteration_duration(CancellationToken token)
    {
        TestKademlia kademlia = new() { LookupDelay = TimeSpan.FromMilliseconds(1100) };
        RandomWalkKademliaDiscovery<int, int, int> discovery = new(
            kademlia,
            IntKeyOperator.Instance,
            Int32KademliaDistance.Instance,
            new KademliaConfig<int> { CurrentNodeId = 0 });

        Stopwatch stopwatch = Stopwatch.StartNew();
        List<int> nodes = await discovery.DiscoverNodes(1, 2, token).Take(3).ToListAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodes, Is.EqualTo(new[] { 1, 2, 1 }));
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2.7)));
        }
    }

    [Test]
    public void Pacing_should_back_off_when_healthy_table_lookup_adds_no_nodes()
    {
        RandomWalkDiscoveryPacingState pacing = new(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(200));
        RandomWalkDiscoveryOccupancy fullTable = new(NodeCount: 2, Capacity: 2);

        Assert.That(pacing.GetNextIterationDuration(false, 0, fullTable), Is.EqualTo(TimeSpan.FromMilliseconds(60)));
        Assert.That(pacing.GetNextIterationDuration(false, 0, fullTable), Is.EqualTo(TimeSpan.FromMilliseconds(120)));
        Assert.That(pacing.GetNextIterationDuration(false, 0, fullTable), Is.EqualTo(TimeSpan.FromMilliseconds(200)));
    }

    [Test]
    public void Pacing_should_keep_minimum_duration_when_table_is_underfilled()
    {
        RandomWalkDiscoveryPacingState pacing = new(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(200));
        RandomWalkDiscoveryOccupancy underfilledTable = new(NodeCount: 1, Capacity: 2);

        Assert.That(pacing.GetNextIterationDuration(false, 0, underfilledTable), Is.EqualTo(TimeSpan.FromMilliseconds(30)));
        Assert.That(pacing.GetNextIterationDuration(false, 0, underfilledTable), Is.EqualTo(TimeSpan.FromMilliseconds(30)));
    }

    [Test]
    public void Pacing_should_reset_when_lookup_adds_node()
    {
        RandomWalkDiscoveryPacingState pacing = new(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(200));
        RandomWalkDiscoveryOccupancy fullTable = new(NodeCount: 2, Capacity: 2);

        Assert.That(pacing.GetNextIterationDuration(false, 0, fullTable), Is.EqualTo(TimeSpan.FromMilliseconds(60)));
        Assert.That(pacing.GetNextIterationDuration(false, 1, fullTable), Is.EqualTo(TimeSpan.FromMilliseconds(30)));
    }

    [Test]
    public void Pacing_should_saturate_without_overflow()
    {
        TimeSpan minimum = TimeSpan.FromTicks((TimeSpan.MaxValue.Ticks / 2) + 1);
        RandomWalkDiscoveryPacingState pacing = new(minimum, TimeSpan.MaxValue);
        RandomWalkDiscoveryOccupancy fullTable = new(NodeCount: 2, Capacity: 2);

        Assert.That(pacing.GetNextIterationDuration(false, 0, fullTable), Is.EqualTo(TimeSpan.MaxValue));
    }

    private sealed class TestKademlia : IKademlia<int, int>
    {
        public event EventHandler<int>? OnNodeAdded;
        public event EventHandler<int>? OnNodeRemoved { add { } remove { } }

        public int LookupNodesCalls { get; private set; }
        public int? LastMaxResults { get; private set; }
        public TimeSpan LookupDelay { get; set; }
        public int[] LookupResults { get; set; } = [1, 2];
        public bool RaiseNodeAddedEvents { get; set; } = true;

        public void AddOrRefresh(int node) => throw new NotSupportedException();

        public void Remove(int node) => throw new NotSupportedException();

        public Task Run(CancellationToken token) => throw new NotSupportedException();

        public Task Bootstrap(CancellationToken token) => throw new NotSupportedException();

        public Task<int[]> LookupNodesClosest(int key, CancellationToken token, int? k = null) => throw new NotSupportedException();

        public IAsyncEnumerable<int> LookupNodes(int key, CancellationToken token, int? maxResults = null, Action? onNodeAdded = null)
        {
            LookupNodesCalls++;
            LastMaxResults = maxResults;
            return CreateLookupNodes(onNodeAdded, token);
        }

        public int[] GetKNeighbour(int target, int excluding = 0, bool excludeSelf = false) => throw new NotSupportedException();

        public int[] GetAllAtDistance(int distance) => throw new NotSupportedException();

        public IEnumerable<int> IterateNodes() => throw new NotSupportedException();

        private async IAsyncEnumerable<int> CreateLookupNodes(Action? onNodeAdded, [EnumeratorCancellation] CancellationToken token)
        {
            if (LookupDelay > TimeSpan.Zero)
            {
                await Task.Delay(LookupDelay, token);
            }

            for (int i = 0; i < LookupResults.Length; i++)
            {
                await Task.Yield();
                if (RaiseNodeAddedEvents)
                {
                    onNodeAdded?.Invoke();
                    OnNodeAdded?.Invoke(this, LookupResults[i]);
                }

                yield return LookupResults[i];
            }
        }
    }

    private sealed class IntKeyOperator : IKeyOperator<int, int, int>
    {
        public static IntKeyOperator Instance { get; } = new();

        public int GetKey(int node) => node;

        public int GetKeyHash(int key) => key;

        public int CreateRandomKeyAtDistance(int nodePrefix, int depth) => depth;
    }

}
