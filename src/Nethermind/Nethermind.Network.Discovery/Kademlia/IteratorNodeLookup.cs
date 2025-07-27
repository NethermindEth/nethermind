// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using NonBlocking;

namespace Nethermind.Network.Discovery.Discv4;

/// <summary>
/// Special lookup made specially for node discovery as the standard lookup is too slow or unnecessarily parallelized.
/// Instead of returning k closest node, it just returns the nodes that it found along the way and stopped early.
/// This is useful for node discovery as trying to get the k closest node is not completely necessary, as the main goal
/// is to reach all node. The lookup is not parallelized as it is expected to be parallelized at a higher level with
/// each worker having different target to look into.
/// </summary>
public class IteratorNodeLookup<TKey, TNode>(
    IRoutingTable<TNode> routingTable,
    KademliaConfig<TNode> kademliaConfig,
    IKademliaMessageSender<TKey, TNode> msgSender,
    IKeyOperator<TKey, TNode> keyOperator,
    ILogManager logManager) : IIteratorNodeLookup<TKey, TNode> where TNode : notnull
{
    private readonly ILogger _logger = logManager.GetClassLogger<IteratorNodeLookup<TKey, TNode>>();
    private readonly ValueHash256 _currentNodeIdAsHash = keyOperator.GetNodeHash(kademliaConfig.CurrentNodeId);

    // Small lru of unreachable nodes, prevent retrying. Pretty effective, although does not improve discovery overall.
    private readonly LruCache<ValueHash256, DateTimeOffset> _unreacheableNodes = new(256, "");

    // The maximum round per lookup. Higher means that it will 'see' deeper into the network, but come at a latency
    // cost of trying many node for increasingly lower new node.
    private const int MaxRounds = 3;

    // These two dont come into effect as MaxRounds is low.
    private const int MaxNonProgressingRound = 3;
    private const int MinResult = 128;

    private bool SameAsSelf(TNode node)
    {
        return keyOperator.GetNodeHash(node) == _currentNodeIdAsHash;
    }

    public async IAsyncEnumerable<TNode> Lookup(TKey target, [EnumeratorCancellation] CancellationToken token)
    {
        ValueHash256 targetHash = keyOperator.GetKeyHash(target);
        if (_logger.IsDebug) _logger.Debug($"Initiate lookup for hash {targetHash}");

        using var cts = token.CreateChildTokenSource();
        token = cts.Token;

        ConcurrentDictionary<ValueHash256, TNode> queried = new();
        ConcurrentDictionary<ValueHash256, TNode> seen = new();

        IComparer<ValueHash256> comparer = Comparer<ValueHash256>.Create((h1, h2) =>
            Hash256XorUtils.Compare(h1, h2, targetHash));

        // Ordered by lowest distance. Will get popped for next round.
        PriorityQueue<(ValueHash256, TNode), ValueHash256> queryQueue = new(comparer);

        // Used to determine if the worker should stop
        ValueHash256 bestNodeId = ValueKeccak.Zero;
        int closestNodeRound = 0;
        int currentRound = 0;
        int totalResult = 0;

        // Check internal table first
        foreach (TNode node in routingTable.GetKNearestNeighbour(targetHash, null))
        {
            ValueHash256 nodeHash = keyOperator.GetNodeHash(node);
            seen.TryAdd(nodeHash, node);

            queryQueue.Enqueue((nodeHash, node), nodeHash);

            yield return node;

            if (bestNodeId == ValueKeccak.Zero || comparer.Compare(nodeHash, bestNodeId) < 0)
            {
                bestNodeId = nodeHash;
            }
        }

        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!queryQueue.TryDequeue(out (ValueHash256 hash, TNode node) toQuery, out ValueHash256 hash256))
            {
                // No node to query and running query.
                if (_logger.IsTrace) _logger.Trace("Stopping lookup. No node to query.");
                yield break;
            }

            if (SameAsSelf(toQuery.node)) continue;

            queried.TryAdd(toQuery.hash, toQuery.node);
            if (_logger.IsTrace) _logger.Trace($"Query {toQuery.node} at round {currentRound}");

            TNode[]? neighbours = await FindNeighbour(toQuery.node, target, token);
            if (neighbours == null || neighbours?.Length == 0)
            {
                if (_logger.IsTrace) _logger.Trace("Empty result");
                continue;
            }

            int queryIgnored = 0;
            int seenIgnored = 0;
            foreach (TNode neighbour in neighbours!)
            {
                ValueHash256 neighbourHash = keyOperator.GetNodeHash(neighbour);

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
                    if (_logger.IsTrace)
                        _logger.Trace($"Found better neighbour {neighbour} at round {currentRound}.");
                    bestNodeId = neighbourHash;
                    closestNodeRound = currentRound;
                }
            }

            if (_logger.IsTrace)
                _logger.Trace($"Count {neighbours.Length}, queried {queryIgnored}, seen {seenIgnored}");

            if (ShouldStop())
            {
                if (_logger.IsTrace) _logger.Trace("Stopping lookup. No better result.");
                break;
            }
        }

        if (_logger.IsTrace) _logger.Trace("Lookup operation finished.");
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
                if (_logger.IsTrace) _logger.Trace($"No more closer node. Round: {round}, closestNodeRound {closestNodeRound}");
                return true;
            }

            if (round >= MaxRounds)
            {
                return true;
            }

            return false;
        }
    }

    async Task<TNode[]?> FindNeighbour(TNode node, TKey target, CancellationToken token)
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
            if (_logger.IsDebug) _logger.Debug($"Find neighbour op failed. {e}");
            return null;
        }
    }

}
