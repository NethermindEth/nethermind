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
public sealed class RandomWalkKademliaDiscovery<TKey, TNode, TKadKey>(
    IKademlia<TKey, TNode> kademlia,
    IKeyOperator<TKey, TNode, TKadKey> keyOperator,
    IKademliaDistance<TKadKey> distance,
    KademliaConfig<TNode> kademliaConfig,
    ILoggerFactory? loggerFactory = null)
    : IKademliaDiscovery<TKey, TNode>
    where TNode : notnull
    where TKadKey : notnull
{
    private static readonly TimeSpan MinimumIterationDuration = TimeSpan.FromSeconds(1);

    private readonly ILogger _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RandomWalkKademliaDiscovery<TKey, TNode, TKadKey>>();
    private readonly TKadKey _currentNodeHash = keyOperator.GetNodeHash(kademliaConfig.CurrentNodeId);
    private readonly int _maxDistance = distance.MaxDistance;

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
        while (!token.IsCancellationRequested)
        {
            Stopwatch iterationTime = Stopwatch.StartNew();
            try
            {
                int targetDistance = Random.Shared.Next(_maxDistance) + 1;
                TKey target = keyOperator.CreateRandomKeyAtDistance(_currentNodeHash, targetDistance);
                if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug($"Looking up random Kademlia target at distance {targetDistance}.");

                int count = 0;
                await foreach (TNode node in kademlia.LookupNodes(target, token, lookupResultLimit).WithCancellation(token))
                {
                    count++;
                    await writer.WriteAsync(node, token);
                }

                if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug($"Found {count} nodes from random Kademlia lookup.");

                if (iterationTime.Elapsed < MinimumIterationDuration)
                {
                    await Task.Delay(MinimumIterationDuration - iterationTime.Elapsed, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Random Kademlia discovery lookup failed.");
            }
        }
    }
}
