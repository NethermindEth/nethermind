// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nethermind.Kademlia;

/// <summary>
/// Runs active random Kademlia lookups and streams discovered nodes.
/// </summary>
/// <remarks>
/// Jobs iterate at <see cref="MinimumIterationDuration"/> while the routing table is still underfilled. Once the table
/// is healthy, each job backs off to <see cref="MaximumProductiveIterationDuration"/> while nodes are still being
/// admitted. Sustained admission-free windows may extend the interval toward <see cref="MaximumIterationDuration"/>,
/// which bounds worst-case staleness rather than describing the usual steady state. Periodic bootstrap and bucket
/// refresh in <see cref="IKademlia{TKey,TNode}.Run"/> are unaffected.
/// Routing-table occupancy deliberately controls this protocol-independent loop because it cannot observe whether
/// downstream consumers accept emitted nodes or establish peer connections.
/// </remarks>
public sealed class RandomWalkKademliaDiscovery<TKey, TNode, TKadKey>(
    IKademlia<TKey, TNode> kademlia,
    IRoutingTable<TNode, TKadKey> routingTable,
    IKeyOperator<TKey, TNode, TKadKey> keyOperator,
    IKademliaDistance<TKadKey> distance,
    KademliaConfig<TNode> kademliaConfig,
    ILoggerFactory loggerFactory,
    TimeProvider? timeProvider = null)
    : IKademliaDiscovery<TKey, TNode>
    where TNode : notnull
    where TKadKey : notnull
{
    private static readonly TimeSpan MinimumIterationDuration = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Longest interval used while a healthy routing table is still admitting nodes.
    /// </summary>
    /// <remarks>
    /// At the default <c>Discovery.ConcurrentDiscoveryJob</c> of ten jobs this keeps one active random walk starting
    /// about every three seconds, while preventing ordinary routing churn from pinning every job at the one-second
    /// bootstrap pace.
    /// </remarks>
    private static readonly TimeSpan MaximumProductiveIterationDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Longest interval an admission-free job can back off to.
    /// </summary>
    /// <remarks>
    /// Shared admissions normally return a live node to the productive range before this ceiling. The lookup rate is
    /// inversely proportional to the cap, so almost all of the saving is already banked by the first few doublings:
    /// a one-minute cap removes 98% of the one-second crawl rate and this one removes 99.7%, while a half-hour cap
    /// would buy a further 0.3% at six times the worst-case delay. Because a reset only applies to the next iteration
    /// and never wakes a sleeping job, this cap alone bounds how long a job can go without looking up.
    /// </remarks>
    private static readonly TimeSpan MaximumIterationDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Reciprocal of the bucket-slot fill ratio the table must reach before idle lookups may slow down.
    /// </summary>
    /// <remarks>
    /// Buckets only split once they overflow, so a table that saturated its reachable neighbourhood settles above
    /// 90% of its slots. A table holding only a handful of contacts, such as one still bootstrapping or one whose
    /// peers were just evicted for being unresponsive, stays below this ratio and keeps discovering at full speed.
    /// </remarks>
    private const int HealthyOccupancyDivisor = 3;

    public RandomWalkKademliaDiscovery(
        IKademlia<TKey, TNode> kademlia,
        IRoutingTable<TNode, TKadKey> routingTable,
        IKeyOperator<TKey, TNode, TKadKey> keyOperator,
        IKademliaDistance<TKadKey> distance,
        KademliaConfig<TNode> kademliaConfig)
        : this(kademlia, routingTable, keyOperator, distance, kademliaConfig, NullLoggerFactory.Instance)
    {
    }

    private readonly ILogger _logger = loggerFactory.CreateLogger<RandomWalkKademliaDiscovery<TKey, TNode, TKadKey>>();
    private readonly TKadKey _currentNodeHash = keyOperator.GetNodeHash(kademliaConfig.CurrentNodeId);
    private readonly int _maxDistance = distance.MaxDistance;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Lock _occupancyLock = new();
    private long _lastOccupancyTimestamp;
    private bool _hasCachedOccupancy;
    private bool _cachedUnderfilled;

    /// <inheritdoc/>
    public IAsyncEnumerable<TNode> DiscoverNodes(int concurrentDiscoveryJobs, int lookupResultLimit, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(concurrentDiscoveryJobs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lookupResultLimit);

        return DiscoverNodesCore(concurrentDiscoveryJobs, lookupResultLimit, token);
    }

    private async IAsyncEnumerable<TNode> DiscoverNodesCore(
        int concurrentDiscoveryJobs,
        int lookupResultLimit,
        [EnumeratorCancellation] CancellationToken token)
    {
        if (concurrentDiscoveryJobs == 0)
        {
            yield break;
        }

        using CancellationTokenSource disposeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken discoveryToken = disposeCts.Token;
        Channel<TNode> channel = Channel.CreateBounded<TNode>(lookupResultLimit);
        AdmissionCounter admissions = new();

        Task[] discoverTasks = new Task[concurrentDiscoveryJobs];
        for (int i = 0; i < discoverTasks.Length; i++)
        {
            discoverTasks[i] = Task.Run(() => RunDiscoveryJob(channel.Writer, lookupResultLimit, admissions, discoveryToken));
        }

        Task discoverTask = Task.WhenAll(discoverTasks);
        try
        {
            routingTable.OnNodeAdded += admissions.OnNodeAdded;
            await foreach (TNode node in channel.Reader.ReadAllAsync(token))
            {
                yield return node;
            }
        }
        finally
        {
            routingTable.OnNodeAdded -= admissions.OnNodeAdded;
            await disposeCts.CancelAsync();
            channel.Writer.TryComplete();
            try
            {
                await discoverTask;
            }
            catch (OperationCanceledException) when (discoveryToken.IsCancellationRequested)
            {
            }
        }
    }

    private async Task RunDiscoveryJob(ChannelWriter<TNode> writer, int lookupResultLimit, AdmissionCounter admissions, CancellationToken token)
    {
        TimeSpan iterationDuration = MinimumIterationDuration;
        // Carried across iterations so that the window covers the paced wait as well as the lookup; a job at the
        // maximum interval is asleep for nearly all of its iteration, and admissions made then must still reset it.
        long admissionsSeen = admissions.Count;
        while (!token.IsCancellationRequested)
        {
            long iterationStart = _timeProvider.GetTimestamp();
            try
            {
                int targetDistance = Random.Shared.Next(_maxDistance) + 1;
                TKey target = keyOperator.CreateRandomKeyAtDistance(_currentNodeHash, targetDistance);
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"Looking up random Kademlia target at distance {targetDistance}.");

                int count = 0;
                await foreach (TNode node in kademlia.LookupNodes(target, token, lookupResultLimit).WithCancellation(token))
                {
                    count++;
                    await writer.WriteAsync(node, token);
                }

                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"Found {count} nodes from random Kademlia lookup.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Random Kademlia discovery lookup failed.");
            }

            long admissionsNow = admissions.Count;
            iterationDuration = NextIterationDuration(iterationDuration, admissionsNow != admissionsSeen);
            admissionsSeen = admissionsNow;

            TimeSpan elapsed = _timeProvider.GetElapsedTime(iterationStart);
            if (elapsed < iterationDuration)
            {
                try
                {
                    await Task.Delay(iterationDuration - elapsed, _timeProvider, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Returns the pace for the next iteration, retaining a sustainable crawl rate while the table changes.
    /// </summary>
    /// <param name="current">Pace the finished iteration ran at.</param>
    /// <param name="admittedNodes">Whether the routing table admitted a node since the previous iteration decided its pace.</param>
    private TimeSpan NextIterationDuration(TimeSpan current, bool admittedNodes)
    {
        if (IsUnderfilled())
        {
            return MinimumIterationDuration;
        }

        TimeSpan maximum = admittedNodes ? MaximumProductiveIterationDuration : MaximumIterationDuration;
        return TimeSpan.FromTicks(Math.Min(current.Ticks * 2, maximum.Ticks));
    }

    private bool IsUnderfilled()
    {
        lock (_occupancyLock)
        {
            long timestamp = _timeProvider.GetTimestamp();
            // Coalesce discovery jobs because GetOccupancy may walk the full table under its mutation lock.
            if (_hasCachedOccupancy &&
                _timeProvider.GetElapsedTime(_lastOccupancyTimestamp, timestamp) < MinimumIterationDuration)
            {
                return _cachedUnderfilled;
            }

            RoutingTableOccupancy occupancy = routingTable.GetOccupancy();
            _cachedUnderfilled = occupancy.NodeCount * HealthyOccupancyDivisor < occupancy.Capacity;
            _lastOccupancyTimestamp = timestamp;
            _hasCachedOccupancy = true;
            return _cachedUnderfilled;
        }
    }

    /// <summary>
    /// Counts nodes newly admitted into the routing table, so that a job can tell a lookup that grew the table from
    /// one that only re-confirmed nodes it already knew or whose results a full bucket rejected.
    /// </summary>
    /// <remarks>
    /// Jobs compare this counter across their whole iteration, so admissions made by a concurrent job or by inbound
    /// traffic count too. Attributing an admission to the lookup that caused it would mean threading a lookup
    /// identity through every protocol adapter. An admission this job did not cause can therefore keep it at the
    /// sustainable productive pace, but cannot return it to the one-second bootstrap pace once the table is healthy.
    /// </remarks>
    private sealed class AdmissionCounter
    {
        private long _count;

        public long Count => Interlocked.Read(ref _count);

        public void OnNodeAdded(object? sender, TNode node) => Interlocked.Increment(ref _count);
    }
}
