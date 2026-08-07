// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nethermind.Kademlia;

/// <summary>
/// Runs active random Kademlia lookups and streams discovered nodes.
/// </summary>
public sealed class RandomWalkKademliaDiscovery<TKey, TNode, TKadKey> : IKademliaDiscovery<TKey, TNode>
    where TNode : notnull
    where TKadKey : notnull
{
    private readonly IKademlia<TKey, TNode> _kademlia;
    private readonly IKeyOperator<TKey, TNode, TKadKey> _keyOperator;
    private readonly KademliaConfig<TNode> _kademliaConfig;
    private readonly IRoutingTable<TNode, TKadKey>? _routingTable;
    private readonly TimeSpan _minimumIterationDuration;
    private readonly TimeSpan _maximumIdleIterationDuration;

    public RandomWalkKademliaDiscovery(
        IKademlia<TKey, TNode> kademlia,
        IKeyOperator<TKey, TNode, TKadKey> keyOperator,
        IKademliaDistance<TKadKey> distance,
        KademliaConfig<TNode> kademliaConfig)
        : this(kademlia, keyOperator, distance, null, kademliaConfig, NullLoggerFactory.Instance)
    {
    }

    public RandomWalkKademliaDiscovery(
        IKademlia<TKey, TNode> kademlia,
        IKeyOperator<TKey, TNode, TKadKey> keyOperator,
        IKademliaDistance<TKadKey> distance,
        KademliaConfig<TNode> kademliaConfig,
        ILoggerFactory loggerFactory)
        : this(kademlia, keyOperator, distance, null, kademliaConfig, loggerFactory)
    {
    }

    public RandomWalkKademliaDiscovery(
        IKademlia<TKey, TNode> kademlia,
        IKeyOperator<TKey, TNode, TKadKey> keyOperator,
        IKademliaDistance<TKadKey> distance,
        IRoutingTable<TNode, TKadKey>? routingTable,
        KademliaConfig<TNode> kademliaConfig,
        ILoggerFactory loggerFactory)
    {
        _kademlia = kademlia;
        _keyOperator = keyOperator;
        _routingTable = routingTable;
        _kademliaConfig = kademliaConfig;
        _minimumIterationDuration = GetPositiveOrDefault(kademliaConfig.RandomWalkMinimumIterationDuration, TimeSpan.FromSeconds(1));
        _maximumIdleIterationDuration = TimeSpan.FromTicks(Math.Max(
            GetPositiveOrDefault(kademliaConfig.RandomWalkMaximumIdleIterationDuration, TimeSpan.FromMinutes(30)).Ticks,
            _minimumIterationDuration.Ticks));

        _logger = loggerFactory.CreateLogger<RandomWalkKademliaDiscovery<TKey, TNode, TKadKey>>();
        _currentNodeHash = keyOperator.GetNodeHash(kademliaConfig.CurrentNodeId);
        _maxDistance = distance.MaxDistance;
    }

    private readonly ILogger _logger;
    private readonly TKadKey _currentNodeHash;
    private readonly int _maxDistance;

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

        Task[] discoverTasks = new Task[concurrentDiscoveryJobs];
        for (int i = 0; i < discoverTasks.Length; i++)
        {
            discoverTasks[i] = Task.Run(() => RunDiscoveryJob(channel.Writer, lookupResultLimit, discoveryToken));
        }

        Task discoverTask = Task.WhenAll(discoverTasks);
        try
        {
            await foreach (TNode node in channel.Reader.ReadAllAsync(token))
            {
                yield return node;
            }
        }
        finally
        {
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

    private async Task RunDiscoveryJob(ChannelWriter<TNode> writer, int lookupResultLimit, CancellationToken token)
    {
        RandomWalkDiscoveryPacingState pacingState = new(_minimumIterationDuration, _maximumIdleIterationDuration);
        while (!token.IsCancellationRequested)
        {
            Stopwatch iterationTime = Stopwatch.StartNew();
            int addedNodeCount = 0;
            bool lookupFailed = false;
            try
            {
                int targetDistance = Random.Shared.Next(_maxDistance) + 1;
                TKey target = _keyOperator.CreateRandomKeyAtDistance(_currentNodeHash, targetDistance);
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"Looking up random Kademlia target at distance {targetDistance}.");

                int count = 0;
                await foreach (TNode node in _kademlia.LookupNodes(
                    target,
                    token,
                    lookupResultLimit,
                    () => Interlocked.Increment(ref addedNodeCount)).WithCancellation(token))
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
                lookupFailed = true;
                _logger.LogError(ex, "Random Kademlia discovery lookup failed.");
            }

            TimeSpan elapsed = iterationTime.Elapsed;
            TimeSpan nextIterationDuration = pacingState.GetNextIterationDuration(
                lookupFailed,
                Volatile.Read(ref addedNodeCount),
                GetRoutingTableOccupancy());
            if (elapsed < nextIterationDuration)
            {
                try
                {
                    await Task.Delay(nextIterationDuration - elapsed, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private RandomWalkDiscoveryOccupancy GetRoutingTableOccupancy()
    {
        if (_routingTable is not null)
        {
            RoutingTableStats stats = _routingTable.GetStats();
            return new RandomWalkDiscoveryOccupancy(stats.NodeCount, stats.BucketCount * _kademliaConfig.KSize);
        }

        return new RandomWalkDiscoveryOccupancy(0, 0);
    }

    private static TimeSpan GetPositiveOrDefault(TimeSpan value, TimeSpan defaultValue)
        => value > TimeSpan.Zero ? value : defaultValue;
}

internal readonly record struct RandomWalkDiscoveryOccupancy(int NodeCount, int Capacity)
{
    public int Percent => Capacity == 0 ? 0 : (int)Math.Min(100, (long)NodeCount * 100 / Capacity);
}

internal sealed class RandomWalkDiscoveryPacingState(TimeSpan minimumIterationDuration, TimeSpan maximumIdleIterationDuration)
{
    private const int HealthyTableOccupancyPercent = 85;
    private const int UnhealthyTableOccupancyPercent = 75;

    private TimeSpan _iterationDuration;
    private bool _tableHealthy;

    public TimeSpan GetNextIterationDuration(bool lookupFailed, int addedNodeCount, RandomWalkDiscoveryOccupancy occupancy)
    {
        if (_iterationDuration == TimeSpan.Zero)
        {
            _iterationDuration = minimumIterationDuration;
        }

        UpdateTableHealth(occupancy);
        if (!lookupFailed && (addedNodeCount > 0 || !_tableHealthy))
        {
            _iterationDuration = minimumIterationDuration;
            return _iterationDuration;
        }

        if (_iterationDuration.Ticks >= maximumIdleIterationDuration.Ticks / 2)
        {
            _iterationDuration = maximumIdleIterationDuration;
            return _iterationDuration;
        }

        _iterationDuration = TimeSpan.FromTicks(_iterationDuration.Ticks * 2);
        return _iterationDuration;
    }

    private void UpdateTableHealth(RandomWalkDiscoveryOccupancy occupancy)
    {
        if (occupancy.Percent >= HealthyTableOccupancyPercent)
        {
            _tableHealthy = true;
            return;
        }

        if (occupancy.Percent <= UnhealthyTableOccupancyPercent)
        {
            _tableHealthy = false;
        }
    }
}
