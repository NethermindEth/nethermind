// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Logging;
using Nethermind.Network.Discovery.Kademlia;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Nethermind.Network.Discovery.Discv4;

/// <summary>
/// Special lookup made specially for node discovery as the standard lookup is too slow or unnecessarily parallelized.
/// Instead of returning k closest node, it just returns the nodes that it found along the way and stopped early.
/// This is useful for node discovery as trying to get the k closest node is not completely necessary, as the main goal
/// is to reach all node. The lookup is not parallelized as it is expected to be parallelized at a higher level with
/// each worker having different target to look into.
/// </summary>
public class IteratorNodeLookup<TPublicKey, THash, TNode>(
    IRoutingTable<THash, TNode> routingTable,
    KademliaConfig<TNode> kademliaConfig,
    IKademliaMessageSender<TPublicKey, TNode> msgSender,
    IKeyOperator<TPublicKey, THash, TNode> keyOperator,
    ILoggerFactory logManager) : IIteratorNodeLookup<TPublicKey, TNode> where TNode : notnull where THash : struct, IKademiliaHash<THash>
{
    private readonly ILogger _logger = logManager.CreateLogger<IteratorNodeLookup<TPublicKey, THash, TNode>>();
    private readonly THash _currentNodeIdAsHash = keyOperator.GetNodeHash(kademliaConfig.CurrentNodeId);

    // Small lru of unreachable nodes, prevent retrying. Pretty effective, although does not improve discovery overall.
    private readonly LruCache<THash, DateTimeOffset> _unreacheableNodes = new(256, "");

    // The maximum round per lookup. Higher means that it will 'see' deeper into the network, but come at a latency
    // cost of trying many node for increasingly lower new node.
    private const int MaxRounds = 3;

    // These two dont come into effect as MaxRounds is low.
    private const int MaxNonProgressingRound = 3;
    private const int MinResult = 128;

    private bool SameAsSelf(TNode node)
    {
        return keyOperator.GetNodeHash(node).Equals(_currentNodeIdAsHash);
    }

    public async IAsyncEnumerable<TNode> Lookup(TPublicKey target, [EnumeratorCancellation] CancellationToken token)
    {
        THash targetHash = keyOperator.GetKeyHash(target);
        if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug($"Initiate lookup for hash {targetHash}");

        using var cts = token.CreateChildTokenSource();
        token = cts.Token;

        ConcurrentDictionary<THash, TNode> queried = new();
        ConcurrentDictionary<THash, TNode> seen = new();

        IComparer<THash> comparer = Comparer<THash>.Create((h1, h2) =>
            THash.Compare(h1, h2, targetHash));

        // Ordered by lowest distance. Will get popped for next round.
        PriorityQueue<(THash, TNode), THash> queryQueue = new(comparer);

        // Used to determine if the worker should stop
        THash bestNodeId = THash.Zero;
        int closestNodeRound = 0;
        int currentRound = 0;
        int totalResult = 0;

        // Check internal table first
        foreach (TNode node in routingTable.GetKNearestNeighbour(targetHash, null))
        {
            THash nodeHash = keyOperator.GetNodeHash(node);
            seen.TryAdd(nodeHash, node);

            queryQueue.Enqueue((nodeHash, node), nodeHash);

            yield return node;

            if (bestNodeId.Equals(THash.Zero) || comparer.Compare(nodeHash, bestNodeId) < 0)
            {
                bestNodeId = nodeHash;
            }
        }

        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!queryQueue.TryDequeue(out (THash hash, TNode node) toQuery, out THash hash256))
            {
                // No node to query and running query.
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Stopping lookup. No node to query.");
                yield break;
            }

            if (SameAsSelf(toQuery.node)) continue;

            queried.TryAdd(toQuery.hash, toQuery.node);
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"Query {toQuery.node} at round {currentRound}");

            TNode[]? neighbours = await FindNeighbour(toQuery.node, target, token);
            if (neighbours == null || neighbours?.Length == 0)
            {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Empty result");
                continue;
            }

            int queryIgnored = 0;
            int seenIgnored = 0;
            foreach (TNode neighbour in neighbours!)
            {
                THash neighbourHash = keyOperator.GetNodeHash(neighbour);

                // Already queried, we ignore
                if (queried.ContainsKey(neighbourHash))
                {
                    queryIgnored++;
                    continue;
                }

                // When seen already dont record
                if (!seen.TryAdd(neighbourHash, neighbour))
                {
                    seenIgnored++;
                    continue;
                }

                totalResult++;
                yield return neighbour;

                bool foundBetter = comparer.Compare(neighbourHash, bestNodeId) < 0;
                queryQueue.Enqueue((neighbourHash, neighbour), neighbourHash);

                // If found a better node, reset closes node round.
                // This causes `ShouldStopDueToNoBetterResult` to return false.
                if (closestNodeRound < currentRound && foundBetter)
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                        _logger.LogTrace($"Found better neighbour {neighbour} at round {currentRound}.");
                    bestNodeId = neighbourHash;
                    closestNodeRound = currentRound;
                }
            }

            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace($"Count {neighbours.Length}, queried {queryIgnored}, seen {seenIgnored}");

            if (ShouldStop())
            {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Stopping lookup. No better result.");
                break;
            }
        }

        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Lookup operation finished.");
        yield break;

        bool ShouldStop()
        {
            int round = ++currentRound;
            if (totalResult >= MinResult && round - closestNodeRound >= MaxNonProgressingRound)
            {
                // No closer node for more than or equal to _alpha*2 round.
                // Assume exit condition
                // Why not just _alpha?
                // Because there could be currently running work that may increase closestNodeRound.
                // So including this worker, assume no more
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"No more closer node. Round: {round}, closestNodeRound {closestNodeRound}");
                return true;
            }

            if (round >= MaxRounds)
            {
                return true;
            }

            return false;
        }
    }

    async Task<TNode[]?> FindNeighbour(TNode node, TPublicKey target, CancellationToken token)
    {
        try
        {
            if (_unreacheableNodes.TryGet(keyOperator.GetNodeHash(node), out var lastAttempt) &&
                lastAttempt + TimeSpan.FromMinutes(5) > DateTimeOffset.Now)
            {
                return [];
            }

            return await msgSender.FindNeighbours(node, target, token);
        }
        catch (OperationCanceledException)
        {
            _unreacheableNodes.Set(keyOperator.GetNodeHash(node), DateTimeOffset.Now);
            return null;
        }
        catch (Exception e)
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug($"Find neighbour op failed. {e}");
            return null;
        }
    }

}


public static class CancellationTokenExtensions
{
    public readonly struct AutoCancelTokenSource(CancellationTokenSource cancellationTokenSource) : IDisposable
    {
        public AutoCancelTokenSource()
            : this(new CancellationTokenSource())
        {
        }

        public CancellationToken Token => cancellationTokenSource.Token;

        public static AutoCancelTokenSource ThatCancelAfter(TimeSpan delay)
        {
            CancellationTokenSource cancellationTokenSource = new();
            cancellationTokenSource.CancelAfter(delay);
            return new AutoCancelTokenSource(cancellationTokenSource);
        }

        public void Dispose()
        {
            cancellationTokenSource.Cancel();
            cancellationTokenSource.Dispose();
        }

        public async Task WhenAllSucceed(params IReadOnlyList<Task> allTasks)
        {
            CancellationTokenSource source = cancellationTokenSource;

            await Task.WhenAll(allTasks.Select(CancelTokenSourceOnError));

            async Task CancelTokenSourceOnError(Task innerTask)
            {
                try
                {
                    await innerTask;
                }
                catch (Exception)
                {
                    await source.CancelAsync();
                    throw;
                }
            }
        }
    }

    internal static AutoCancelTokenSource CreateChildTokenSource(this CancellationToken parentToken, TimeSpan delay = default)
    {
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        if (delay != TimeSpan.Zero) cts.CancelAfter(delay);

        return new AutoCancelTokenSource(cts);
    }
}
