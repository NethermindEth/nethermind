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
using Autofac;
using Nethermind.Core;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
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
        using IContainer container = CreateContainer(kademlia, new RoutingTableStub());
        IKademliaDiscovery<int, int> discovery = container.Resolve<IKademliaDiscovery<int, int>>();

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
        using IContainer container = CreateContainer(kademlia, new RoutingTableStub());
        IKademliaDiscovery<int, int> discovery = container.Resolve<IKademliaDiscovery<int, int>>();

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
        using IContainer container = CreateContainer(kademlia, new RoutingTableStub());
        IKademliaDiscovery<int, int> discovery = container.Resolve<IKademliaDiscovery<int, int>>();

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
        using IContainer container = CreateContainer(kademlia, new RoutingTableStub());
        IKademliaDiscovery<int, int> discovery = container.Resolve<IKademliaDiscovery<int, int>>();

        Stopwatch stopwatch = Stopwatch.StartNew();
        List<int> nodes = await discovery.DiscoverNodes(1, 2, token).Take(3).ToListAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodes, Is.EqualTo(new[] { 1, 2, 1 }));
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2.7)));
        }
    }

    [TestCase(15, 16)]
    [TestCase(21, 64)]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_should_keep_minimum_pace_while_table_is_underfilled(int nodeCount, int capacity, CancellationToken token)
    {
        RoutingTableStub routingTable = new() { Occupancy = new RoutingTableOccupancy(nodeCount, capacity) };

        TimeSpan[] delays = await RunIterations(new TestKademlia(), routingTable, iterations: 4, token);

        AssertPacedBy(delays, [OneSecond, OneSecond, OneSecond]);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_should_back_off_when_filled_table_admits_nothing([Values(16, 48)] int capacity, CancellationToken token)
    {
        RoutingTableStub routingTable = new() { Occupancy = new RoutingTableOccupancy(16, capacity) };

        TimeSpan[] delays = await RunIterations(new TestKademlia(), routingTable, iterations: 11, token);

        AssertPacedBy(delays, [
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16),
            TimeSpan.FromSeconds(32), TimeSpan.FromSeconds(64), TimeSpan.FromSeconds(128), TimeSpan.FromSeconds(256),
            // Doubling stops at the cap.
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)
        ]);
    }

    [TestCaseSource(nameof(ProductivePacingCases))]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_should_bound_productive_filled_table_pace(
        int[] admittingLookups,
        int iterations,
        int[] expectedDelaySeconds,
        CancellationToken token)
    {
        RoutingTableStub routingTable = new() { Occupancy = FilledTable };
        TestKademlia kademlia = new()
        {
            OnLookup = lookup =>
            {
                if (Array.IndexOf(admittingLookups, lookup) >= 0)
                {
                    routingTable.RaiseNodeAdded(42);
                }
            }
        };

        TimeSpan[] delays = await RunIterations(kademlia, routingTable, iterations, token);

        AssertPacedBy(delays, Array.ConvertAll(expectedDelaySeconds, static seconds => TimeSpan.FromSeconds(seconds)));
    }

    /// <summary>
    /// A backed-off job spends nearly all of its iteration waiting, so an admission arriving from inbound traffic or
    /// another job while it waits has to restore productive pacing just as one during its own lookup does.
    /// </summary>
    [Test]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_should_return_to_productive_pace_when_a_node_is_admitted_while_waiting(CancellationToken token)
    {
        RoutingTableStub routingTable = new() { Occupancy = FilledTable };

        TimeSpan[] delays = await RunIterations(new TestKademlia(), routingTable, iterations: 8, token,
            onDelayRequested: wait => { if (wait == 5) routingTable.RaiseNodeAdded(42); });

        AssertPacedBy(delays, [
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16),
            TimeSpan.FromSeconds(32), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)
        ]);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_should_return_to_minimum_pace_when_table_becomes_underfilled(CancellationToken token)
    {
        RoutingTableStub routingTable = new() { Occupancy = FilledTable };

        TimeSpan[] delays = await RunIterations(new TestKademlia(), routingTable, iterations: 8, token,
            onDelayRequested: wait =>
            {
                if (wait == 5)
                {
                    routingTable.Occupancy = new RoutingTableOccupancy(5, 16);
                }
            });

        AssertPacedBy(delays, [
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16),
            TimeSpan.FromSeconds(32), OneSecond, OneSecond
        ]);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_should_share_cached_routing_table_occupancy_between_jobs(CancellationToken token)
    {
        RoutingTableStub routingTable = new() { Occupancy = FilledTable };

        await RunPacingChecks(routingTable, checks: 4, token, concurrentJobs: 4);

        Assert.That(routingTable.GetOccupancyCalls, Is.EqualTo(1));
    }

    [TestCase(999, 1, 4)]
    [TestCase(1000, 2, 1)]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_should_expire_cached_routing_table_occupancy_after_one_second(
        int elapsedMilliseconds,
        int expectedOccupancyCalls,
        int expectedDelaySeconds,
        CancellationToken token)
    {
        RoutingTableStub routingTable = new() { Occupancy = FilledTable };

        TimeSpan[] delays = await RunPacingChecks(routingTable, checks: 2, token,
            timeAdvance: TimeSpan.FromMilliseconds(elapsedMilliseconds),
            onDelayRequested: wait => { if (wait == 1) routingTable.Occupancy = new RoutingTableOccupancy(0, 16); });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(routingTable.GetOccupancyCalls, Is.EqualTo(expectedOccupancyCalls));
            Assert.That(delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(expectedDelaySeconds) }));
        }
    }

    private static async Task<TimeSpan[]> RunPacingChecks(
        RoutingTableStub routingTable,
        int checks,
        CancellationToken token,
        int concurrentJobs = 1,
        TimeSpan? timeAdvance = null,
        Action<int>? onDelayRequested = null)
    {
        TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NoWaitTimeProvider timeProvider = new()
        {
            TimeAdvance = timeAdvance ?? TimeSpan.Zero,
            CompletedTimers = checks - concurrentJobs,
            OnDelayRequested = wait =>
            {
                onDelayRequested?.Invoke(wait);
                if (wait == checks) completed.TrySetResult();
            }
        };
        using IContainer container = CreateContainer(new TestKademlia(), routingTable, timeProvider);
        IKademliaDiscovery<int, int> discovery = container.Resolve<IKademliaDiscovery<int, int>>();

        // Leave enough channel space for every lookup and park each job only after it has checked occupancy.
        await using IAsyncEnumerator<int> nodes = discovery.DiscoverNodes(concurrentJobs, checks * NodesPerLookup, token).GetAsyncEnumerator(token);
        Assert.That(await nodes.MoveNextAsync(), Is.True);
        await completed.Task.WaitAsync(token);
        return timeProvider.RequestedDelays;
    }

    /// <summary>Asserts that the first iterations waited for exactly the expected paces.</summary>
    private static void AssertPacedBy(TimeSpan[] delays, TimeSpan[] expected) =>
        Assert.That(delays[..expected.Length], Is.EqualTo(expected));

    private static IEnumerable<TestCaseData> ProductivePacingCases()
    {
        yield return new TestCaseData(
                new[] { 6 },
                8,
                new[] { 2, 4, 8, 16, 32, 30, 60 })
            .SetName("DiscoverNodes_returns_to_productive_pace_when_lookup_admits_a_node");
        yield return new TestCaseData(
                new[] { 1, 2, 3, 4, 5, 6, 7 },
                7,
                new[] { 2, 4, 8, 16, 30, 30 })
            .SetName("DiscoverNodes_limits_continuously_productive_filled_table_pace");
        yield return new TestCaseData(
                new[] { 10 },
                12,
                new[] { 2, 4, 8, 16, 32, 64, 128, 256, 300, 30, 60 })
            .SetName("DiscoverNodes_returns_from_idle_ceiling_to_productive_pace");
    }

    private static IContainer CreateContainer(
        TestKademlia kademlia,
        RoutingTableStub routingTable,
        TimeProvider? timeProvider = null) =>
        new ContainerBuilder()
            .AddModule(new KademliaModule<int, int, int>())
            .AddSingleton<IKademlia<int, int>>(kademlia)
            .AddSingleton<IRoutingTable<int, int>>(routingTable)
            .AddSingleton<IKeyOperator<int, int, int>>(IntKeyOperator.Instance)
            .AddSingleton<IKademliaDistance<int>>(Int32KademliaDistance.Instance)
            .AddSingleton(new KademliaConfig<int> { CurrentNodeId = 0 })
            .AddSingleton(timeProvider ?? TimeProvider.System)
            .Build();

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
        NoWaitTimeProvider timeProvider = new()
        {
            OnDelayRequested = onDelayRequested
        };
        using IContainer container = CreateContainer(kademlia, routingTable, timeProvider);
        IKademliaDiscovery<int, int> discovery = container.Resolve<IKademliaDiscovery<int, int>>();

        await discovery.DiscoverNodes(1, NodesPerLookup, token)
            .Take(iterations * NodesPerLookup).ToListAsync(token);

        return timeProvider.RequestedDelays;
    }

    /// <summary>
    /// Records paced delays and advances the clock only when a timer is requested.
    /// </summary>
    /// <remarks>
    /// Lookup time stays zero regardless of the test machine's speed. Timers beyond <see cref="CompletedTimers"/>
    /// stay pending until discovery is disposed, allowing tests to inspect a fixed number of completed pacing checks.
    /// </remarks>
    private sealed class NoWaitTimeProvider : TimeProvider
    {
        private readonly ConcurrentQueue<TimeSpan> _requestedDelays = new();
        private long _timestamp;
        private int _timerCount;

        public TimeSpan[] RequestedDelays => _requestedDelays.ToArray();

        public int CompletedTimers { get; init; } = int.MaxValue;

        public TimeSpan? TimeAdvance { get; init; }

        /// <summary>Called as a job starts waiting, with the one-based ordinal of that wait.</summary>
        public Action<int>? OnDelayRequested { get; init; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _requestedDelays.Enqueue(dueTime);
            int timer = Interlocked.Increment(ref _timerCount);
            Interlocked.Add(ref _timestamp, (TimeAdvance ?? dueTime).Ticks);
            OnDelayRequested?.Invoke(timer);
            return System.CreateTimer(callback, state, timer <= CompletedTimers ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, period);
        }
    }

    private sealed class RoutingTableStub : IRoutingTable<int, int>
    {
        private int _getOccupancyCalls;

        public RoutingTableOccupancy Occupancy { get; set; } = new(0, 16);

        public int GetOccupancyCalls => Volatile.Read(ref _getOccupancyCalls);

        public RoutingTableOccupancy GetOccupancy()
        {
            Interlocked.Increment(ref _getOccupancyCalls);
            return Occupancy;
        }

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
