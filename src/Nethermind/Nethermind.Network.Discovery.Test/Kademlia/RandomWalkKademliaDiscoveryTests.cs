// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.Kademlia;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class RandomWalkKademliaDiscoveryTests
{
    private const int NodesPerLookup = 2;

    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    /// <summary>A table whose buckets are filled well past the ratio that lets idle lookups back off.</summary>
    private static readonly RoutingTableOccupancy FilledTable = new(16, 16);

    [Test]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_should_stream_nodes_from_random_lookup(CancellationToken token)
    {
        TestKademlia kademlia = new();
        RandomWalkKademliaDiscovery<int, int, int> discovery = CreateDiscovery(kademlia, new RoutingTableStub());

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
    public async Task DiscoverNodes_should_not_run_any_job_when_disabled(CancellationToken token)
    {
        TestKademlia kademlia = new();
        RandomWalkKademliaDiscovery<int, int, int> discovery = CreateDiscovery(kademlia, new RoutingTableStub());

        List<int> nodes = await discovery.DiscoverNodes(0, 2, token).ToListAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodes, Is.Empty);
            Assert.That(kademlia.LookupNodesCalls, Is.Zero);
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_should_pace_iterations_to_minimum_iteration_duration(CancellationToken token)
    {
        TestKademlia kademlia = new();
        RandomWalkKademliaDiscovery<int, int, int> discovery = CreateDiscovery(kademlia, new RoutingTableStub());

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
        RandomWalkKademliaDiscovery<int, int, int> discovery = CreateDiscovery(kademlia, new RoutingTableStub());

        Stopwatch stopwatch = Stopwatch.StartNew();
        List<int> nodes = await discovery.DiscoverNodes(1, 2, token).Take(3).ToListAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodes, Is.EqualTo(new[] { 1, 2, 1 }));
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2.7)));
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_should_keep_minimum_pace_while_table_is_underfilled(CancellationToken token)
    {
        RoutingTableStub routingTable = new() { Occupancy = new RoutingTableOccupancy(5, 16) };

        TimeSpan[] delays = await RunIterations(new TestKademlia(), routingTable, iterations: 4, token);

        AssertPacedBy(delays, [OneSecond, OneSecond, OneSecond]);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_should_back_off_when_filled_table_admits_nothing(CancellationToken token)
    {
        RoutingTableStub routingTable = new() { Occupancy = FilledTable };

        TimeSpan[] delays = await RunIterations(new TestKademlia(), routingTable, iterations: 11, token);

        AssertPacedBy(delays, [
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16),
            TimeSpan.FromSeconds(32), TimeSpan.FromSeconds(64), TimeSpan.FromSeconds(128), TimeSpan.FromSeconds(256),
            // Doubling stops at the cap.
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)
        ]);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_should_reset_pace_when_lookup_admits_a_node(CancellationToken token)
    {
        RoutingTableStub routingTable = new() { Occupancy = FilledTable };
        TestKademlia kademlia = new() { OnLookup = lookup => { if (lookup == 3) routingTable.RaiseNodeAdded(42); } };

        TimeSpan[] delays = await RunIterations(kademlia, routingTable, iterations: 5, token);

        AssertPacedBy(delays, [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), OneSecond, TimeSpan.FromSeconds(2)]);
    }

    /// <summary>
    /// A backed-off job spends nearly all of its iteration waiting, so an admission arriving from inbound traffic or
    /// another job while it waits has to reset it just as one during its own lookup does.
    /// </summary>
    [Test]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_should_reset_pace_when_a_node_is_admitted_while_waiting(CancellationToken token)
    {
        RoutingTableStub routingTable = new() { Occupancy = FilledTable };

        TimeSpan[] delays = await RunIterations(new TestKademlia(), routingTable, iterations: 5, token,
            onDelayRequested: wait => { if (wait == 2) routingTable.RaiseNodeAdded(42); });

        AssertPacedBy(delays, [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), OneSecond, TimeSpan.FromSeconds(2)]);
    }

    /// <summary>Asserts that the first iterations waited for exactly the expected paces.</summary>
    private static void AssertPacedBy(TimeSpan[] delays, TimeSpan[] expected) =>
        Assert.That(delays.Take(expected.Length).ToArray(), Is.EqualTo(expected));

    private static RandomWalkKademliaDiscovery<int, int, int> CreateDiscovery(
        TestKademlia kademlia,
        RoutingTableStub routingTable,
        TimeProvider? timeProvider = null) =>
        new(kademlia,
            routingTable,
            IntKeyOperator.Instance,
            Int32KademliaDistance.Instance,
            new KademliaConfig<int> { CurrentNodeId = 0 },
            NullLoggerFactory.Instance,
            timeProvider);

    /// <summary>
    /// Runs the requested number of lookup iterations and returns the paced delays each of them asked for.
    /// </summary>
    /// <remarks>
    /// Delays are requested before the wait starts, so consuming the nodes of iteration n guarantees that the delays
    /// of every earlier iteration have been recorded.
    /// </remarks>
    private static async Task<TimeSpan[]> RunIterations(
        TestKademlia kademlia,
        RoutingTableStub routingTable,
        int iterations,
        CancellationToken token,
        Action<int>? onDelayRequested = null)
    {
        NoWaitTimeProvider timeProvider = new() { OnDelayRequested = onDelayRequested };
        RandomWalkKademliaDiscovery<int, int, int> discovery = CreateDiscovery(kademlia, routingTable, timeProvider);

        await discovery.DiscoverNodes(1, NodesPerLookup, token).Take(iterations * NodesPerLookup).ToListAsync(token);

        return timeProvider.RequestedDelays;
    }

    /// <summary>
    /// Runs timers immediately while recording the delay that was asked for, on a clock that never advances.
    /// </summary>
    /// <remarks>
    /// Freezing <see cref="GetTimestamp"/> makes the measured lookup time zero, so a job asks for exactly the pace it
    /// chose rather than the pace minus however long the test machine took.
    /// </remarks>
    private sealed class NoWaitTimeProvider : TimeProvider
    {
        private readonly ConcurrentQueue<TimeSpan> _requestedDelays = new();

        public TimeSpan[] RequestedDelays => _requestedDelays.ToArray();

        /// <summary>Called as a job starts waiting, with the one-based ordinal of that wait.</summary>
        public Action<int>? OnDelayRequested { get; init; }

        public override long GetTimestamp() => 0;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _requestedDelays.Enqueue(dueTime);
            OnDelayRequested?.Invoke(_requestedDelays.Count);
            return System.CreateTimer(callback, state, TimeSpan.Zero, period);
        }
    }

    private sealed class RoutingTableStub : IRoutingTable<int, int>
    {
        public RoutingTableOccupancy Occupancy { get; init; } = new(0, 16);

        public RoutingTableOccupancy GetOccupancy() => Occupancy;

        public void RaiseNodeAdded(int node) => OnNodeAdded?.Invoke(this, node);

        public event EventHandler<int>? OnNodeAdded;

        public event EventHandler<int>? OnNodeRemoved
        {
            add { }
            remove { }
        }

        public BucketAddResult TryAddOrRefresh(in int hash, int item, out int toRefresh) => throw new NotSupportedException();

        public bool Remove(in int hash) => throw new NotSupportedException();

        public int[] GetKNearestNeighbour(int hash, bool excludeSelf = false) => throw new NotSupportedException();

        public int[] GetKNearestNeighbourExcluding(int hash, int exclude, bool excludeSelf = false) => throw new NotSupportedException();

        public int[] GetAllAtDistance(int i) => throw new NotSupportedException();

        public IEnumerable<RoutingTableBucket<int, int>> IterateBuckets() => throw new NotSupportedException();

        public int GetByHash(int nodeId) => throw new NotSupportedException();

        public bool TryGet(in int hash, out int node) => throw new NotSupportedException();

        public void LogDebugInfo() => throw new NotSupportedException();
    }

    private sealed class TestKademlia : IKademlia<int, int>
    {
        private int _lookupNodesCalls;

        public event EventHandler<int>? OnNodeAdded { add { } remove { } }
        public event EventHandler<int>? OnNodeRemoved { add { } remove { } }

        public int LookupNodesCalls => _lookupNodesCalls;
        public int? LastMaxResults { get; private set; }
        public TimeSpan LookupDelay { get; set; }

        /// <summary>Called with the one-based ordinal of each started lookup.</summary>
        public Action<int>? OnLookup { get; init; }

        public void AddOrRefresh(int node) => throw new NotSupportedException();

        public bool TryGetNode(int node, out int storedNode) => throw new NotSupportedException();

        public void Remove(int node) => throw new NotSupportedException();

        public Task Run(CancellationToken token) => throw new NotSupportedException();

        public Task Bootstrap(CancellationToken token) => throw new NotSupportedException();

        public Task<int[]> LookupNodesClosest(int key, CancellationToken token, int? k = null) => throw new NotSupportedException();

        public IAsyncEnumerable<int> LookupNodes(int key, CancellationToken token, int? maxResults = null)
        {
            LastMaxResults = maxResults;
            int lookup = Interlocked.Increment(ref _lookupNodesCalls);
            OnLookup?.Invoke(lookup);
            return CreateAsyncEnumerable(LookupDelay, token, 1, 2);
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

    private static async IAsyncEnumerable<T> CreateAsyncEnumerable<T>(TimeSpan delay, [EnumeratorCancellation] CancellationToken token, params IEnumerable<T> items)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, token);
        }
        foreach (T item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }
}
