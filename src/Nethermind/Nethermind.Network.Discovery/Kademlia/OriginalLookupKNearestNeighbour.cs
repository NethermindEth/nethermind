// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Network.Discovery.Kademlia;

/// <summary>
/// This find nearest k query follows the kademlia paper faithfully, but does not do much parallelism.
/// </summary>
/// <param name="targetHash"></param>
/// <param name="k"></param>
/// <param name="findNeighbourOp"></param>
/// <param name="token"></param>
/// <returns></returns>
public class OriginalLookupKNearestNeighbour<TNode>(
    IRoutingTable<TNode> routingTable,
    INodeHashProvider<TNode> nodeHashProvider,
    KademliaConfig<TNode> config,
    ILogManager logManager): ILookupAlgo<TNode>
{
    private static readonly TimeSpan FindNeighbourHardTimeout = TimeSpan.FromSeconds(5);
    private ILogger _logger = logManager.GetClassLogger<NewLookupKNearestNeighbour<TNode>>();

    public async Task<TNode[]> Lookup(
        ValueHash256 targetHash,
        int k,
        Func<TNode, CancellationToken, Task<TNode[]?>> findNeighbourOp,
        CancellationToken token
    ) {
        if (_logger.IsDebug) _logger.Debug($"Initiate lookup for hash {targetHash}");

        Func<TNode, Task<(TNode target, TNode[]? retVal)>> wrappedFindNeighbourHop = async (node) =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(FindNeighbourHardTimeout);

            try
            {
                // targetHash is implied in findNeighbourOp
                return (node, await findNeighbourOp(node, cts.Token));
            }
            catch (OperationCanceledException)
            {
                return (node, null);
            }
            catch (Exception e)
            {
                _logger.Error($"Find neighbour op failed. {e}");
                return (node, null);
            }
        };

        Dictionary<ValueHash256, TNode> queried = new();
        Dictionary<ValueHash256, TNode> queriedAndResponded = new();
        Dictionary<ValueHash256, TNode> seen = new();

        IComparer<ValueHash256> comparer = Comparer<ValueHash256>.Create((h1, h2) =>
            Hash256XORUtils.Compare(h1, h2, targetHash));

        // Ordered by lowest distance. Will get popped for next round.
        PriorityQueue<TNode, ValueHash256> bestSeen = new (comparer);

        // Ordered by lowest distance. Will not get popped for next round, but will at final collection.
        PriorityQueue<TNode, ValueHash256> bestSeenAllTime = new (comparer);

        ValueHash256 closestNodeHash = nodeHashProvider.GetHash(config.CurrentNodeId);
        (ValueHash256 nodeHash, TNode node)[] roundQuery = routingTable.GetKNearestNeighbour(targetHash, default)
            .Take(config.Alpha)
            .Select((node) => (nodeHashProvider.GetHash(node), node))
            .ToArray();
        foreach ((ValueHash256 nodeHash, TNode node) entry in roundQuery)
        {
            (ValueHash256 nodeHash, TNode node) = entry;
            seen.Add(nodeHash, node);
            bestSeen.Enqueue(node, nodeHash);
            bestSeenAllTime.Enqueue(node, nodeHash);
        }

        while (roundQuery.Length > 0)
        {
            // TODO: The paper mentioned that the next round can start immediately while waiting
            // for the result of previous round.
            token.ThrowIfCancellationRequested();

            foreach (var kv in roundQuery)
            {
                queried.TryAdd(kv.nodeHash, kv.node);
            }

            (TNode NodeId, TNode[]? Neighbours)[] currentRoundResponse = await Task.WhenAll(
                roundQuery.Select((hn) => wrappedFindNeighbourHop(hn.Item2)));

            bool hasCloserThanClosest = false;
            foreach ((TNode NodeId, TNode[]? Neighbours) response in currentRoundResponse)
            {
                if (response.Neighbours == null) continue; // Timeout or failed to get response
                if (_logger.IsTrace) _logger.Trace($"Received {response.Neighbours.Length} from {response.NodeId}");

                queriedAndResponded.TryAdd(nodeHashProvider.GetHash(response.NodeId), response.NodeId);

                foreach (TNode neighbour in response.Neighbours)
                {
                    ValueHash256 neighbourHash = nodeHashProvider.GetHash(neighbour);
                    // Already queried, we ignore
                    if (queried.ContainsKey(neighbourHash)) continue;

                    // When seen already dont record
                    if (!seen.TryAdd(neighbourHash, neighbour)) continue;

                    bestSeen.Enqueue(neighbour, neighbourHash);
                    bestSeenAllTime.Enqueue(neighbour, neighbourHash);

                    if (comparer.Compare(neighbourHash, closestNodeHash) < 0)
                    {
                        hasCloserThanClosest = true;
                        closestNodeHash = neighbourHash;
                    }
                }
            }

            if (!hasCloserThanClosest)
            {
                // end condition it seems
                break;
            }

            int toTake = Math.Min(config.Alpha, bestSeen.Count);
            roundQuery = Enumerable.Range(0, toTake).Select((_) =>
            {
                TNode node = bestSeen.Dequeue();
                return (nodeHashProvider.GetHash(node), node);
            }).ToArray();
        }

        // At this point need to query for the maxNode.
        List<TNode> result = [];
        while (result.Count < k && bestSeenAllTime.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            TNode nextLowest = bestSeenAllTime.Dequeue();
            ValueHash256 nextLowestHash = nodeHashProvider.GetHash(nextLowest);

            if (queriedAndResponded.ContainsKey(nextLowestHash))
            {
                result.Add(nextLowest);
                continue;
            }

            if (queried.ContainsKey(nextLowestHash))
            {
                // Queried but not responded
                continue;
            }

            // TODO: In parallel?
            // So the paper mentioned that node that it need to query findnode for node that was not queried.
            (_, TNode[]? nextCandidate) = await wrappedFindNeighbourHop(nextLowest);
            if (nextCandidate != null)
            {
                result.Add(nextLowest);
            }
        }

        return result.ToArray();
    }
}
