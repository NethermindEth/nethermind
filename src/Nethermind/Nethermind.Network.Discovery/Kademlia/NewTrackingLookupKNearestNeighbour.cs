// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Logging;
using NonBlocking;

namespace Nethermind.Network.Discovery.Kademlia;

public class NewaTrackingLookupKNearestNeighbour<TNode>(
    IRoutingTable<TNode> routingTable,
    INodeHashProvider<TNode> nodeHashProvider,
    KademliaConfig<TNode> kademliaConfig,
    INodeHealthTracker<TNode> nodeHealthTracker,
    KademliaConfig<TNode> config,
    ILogManager logManager) : IITeratorAlgo<TNode> where TNode : notnull
{
    private readonly TimeSpan _findNeighbourHardTimeout = config.LookupFindNeighbourHardTimout;
    private readonly ILogger _logger = logManager.GetClassLogger<NewaTrackingLookupKNearestNeighbour<TNode>>();
    private readonly ValueHash256 _currentNodeIdAsHash = nodeHashProvider.GetHash(kademliaConfig.CurrentNodeId);

    private bool SameAsSelf(TNode node)
    {
        return nodeHashProvider.GetHash(node) == _currentNodeIdAsHash;
    }

    public async IAsyncEnumerable<TNode> Lookup(
        ValueHash256 targetHash,
        int minResult,
        Func<TNode, CancellationToken, Task<TNode[]?>> findNeighbourOp,
        [EnumeratorCancellation] CancellationToken token
    ) {
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
            ValueHash256 nodeHash = nodeHashProvider.GetHash(node);
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

            TNode[]? neighbours = await WrappedFindNeighbourOp(toQuery.node);
            if (neighbours == null || neighbours?.Length == 0)
            {
                if (_logger.IsTrace) _logger.Trace("Empty result");
                continue;
            }

            int queryIgnored = 0;
            int seenIgnored = 0;
            foreach (TNode neighbour in neighbours!)
            {
                ValueHash256 neighbourHash = nodeHashProvider.GetHash(neighbour);

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

                Interlocked.Increment(ref totalResult);
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

            if (ShouldStopDueToNoBetterResult())
            {
                if (_logger.IsTrace) _logger.Trace("Stopping lookup. No better result.");
                break;
            }
        }

        if (_logger.IsTrace) _logger.Trace("Lookup operation finished.");
        yield break;

        async Task<TNode[]?> WrappedFindNeighbourOp(TNode node)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(_findNeighbourHardTimeout);

            try
            {
                // targetHash is implied in findNeighbourOp
                TNode[]? ret = await findNeighbourOp(node, cts.Token);
                nodeHealthTracker.OnIncomingMessageFrom(node);

                return ret;
            }
            catch (OperationCanceledException)
            {
                nodeHealthTracker.OnRequestFailed(node);
                return null;
            }
            catch (Exception e)
            {
                nodeHealthTracker.OnRequestFailed(node);
                if (_logger.IsDebug) _logger.Debug($"Find neighbour op failed. {e}");
                return null;
            }
        }

        bool ShouldStopDueToNoBetterResult()
        {
            int round = Interlocked.Increment(ref currentRound);
            if (totalResult >= minResult && round - closestNodeRound >= (config.Alpha*2))
            {
                // No closer node for more than or equal to _alpha*2 round.
                // Assume exit condition
                // Why not just _alpha?
                // Because there could be currently running work that may increase closestNodeRound.
                // So including this worker, assume no more
                if (_logger.IsTrace) _logger.Trace($"No more closer node. Round: {round}, closestNodeRound {closestNodeRound}");
                return true;
            }

            return false;
        }
    }
}
