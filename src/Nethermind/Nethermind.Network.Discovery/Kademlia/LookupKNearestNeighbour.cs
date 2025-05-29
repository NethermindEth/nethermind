// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Logging;
using NonBlocking;

namespace Nethermind.Network.Discovery.Kademlia;

/// <summary>
/// This find nearest k query does not follow the kademlia paper faithfully. Instead of distinct rounds, it has
/// num worker where alpha is the number of worker. Worker does not wait for other worker. Stop condition
/// happens if no more node to query or no new node can be added to the current result set that can improve it
/// for more than alpha*2 request. It is slightly faster than the legacy query on find value where it can be cancelled
/// earlier as it converge to the content faster, but take more query for findnodes due to a more strict stop
/// condition.
/// </summary>
public class LookupKNearestNeighbour<TKey, TNode>(
    IRoutingTable<TNode> routingTable,
    INodeHashProvider<TNode> nodeHashProvider,
    INodeHealthTracker<TNode> nodeHealthTracker,
    KademliaConfig<TNode> config,
    ILogManager logManager) : ILookupAlgo<TNode> where TNode : notnull
{
    private readonly TimeSpan _findNeighbourHardTimeout = config.LookupFindNeighbourHardTimout;
    private readonly ILogger _logger = logManager.GetClassLogger<LookupKNearestNeighbour<TKey, TNode>>();

    public async Task<TNode[]> Lookup(
        ValueHash256 targetHash,
        int k,
        Func<TNode, CancellationToken, Task<TNode[]?>> findNeighbourOp,
        CancellationToken token
    )
    {
        if (_logger.IsDebug) _logger.Debug($"Initiate lookup for hash {targetHash}");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = cts.Token;

        ConcurrentDictionary<ValueHash256, TNode> queried = new();
        ConcurrentDictionary<ValueHash256, TNode> seen = new();

        IComparer<ValueHash256> comparer = Comparer<ValueHash256>.Create((h1, h2) =>
            Hash256XorUtils.Compare(h1, h2, targetHash));
        IComparer<ValueHash256> comparerReverse = Comparer<ValueHash256>.Create((h1, h2) =>
            Hash256XorUtils.Compare(h2, h1, targetHash));

        McsLock queueLock = new McsLock();

        // Ordered by lowest distance. Will get popped for next round.
        PriorityQueue<(ValueHash256, TNode), ValueHash256> bestSeen = new(comparer);

        // Ordered by highest distance. Added on result. Get popped as result.
        PriorityQueue<(ValueHash256, TNode), ValueHash256> finalResult = new(comparerReverse);

        foreach (TNode node in routingTable.GetKNearestNeighbour(targetHash, default))
        {
            ValueHash256 nodeHash = nodeHashProvider.GetHash(node);
            seen.TryAdd(nodeHash, node);
            bestSeen.Enqueue((nodeHash, node), nodeHash);
        }

        TaskCompletionSource roundComplete = new TaskCompletionSource(token);
        int closestNodeRound = 0;
        int currentRound = 0;
        int queryingTask = 0;
        bool finished = false;

        Task[] worker = Enumerable.Range(0, config.Alpha).Select((i) => Task.Run(async () =>
        {
            while (!finished)
            {
                token.ThrowIfCancellationRequested();
                if (!TryGetNodeToQuery(out (ValueHash256 hash, TNode node)? toQuery))
                {
                    if (queryingTask > 0)
                    {
                        // Need to wait for all querying tasks first here.
                        await Task.WhenAny(roundComplete.Task, Task.Delay(100, token));
                        continue;
                    }

                    // No node to query and running query.
                    if (_logger.IsTrace) _logger.Trace("Stopping lookup. No node to query.");
                    break;
                }

                try
                {
                    if (ShouldStopDueToNoBetterResult(out var round))
                    {
                        if (_logger.IsTrace) _logger.Trace("Stopping lookup. No better result.");
                        break;
                    }

                    queried.TryAdd(toQuery.Value.hash, toQuery.Value.node);
                    (TNode, TNode[]? neighbours)? result = await WrappedFindNeighbourOp(toQuery.Value.node);
                    if (result == null) continue;

                    ProcessResult(toQuery.Value.hash, toQuery.Value.node, result, round);
                }
                finally
                {
                    Interlocked.Decrement(ref queryingTask);
                    if (roundComplete.TrySetResult()) roundComplete = new TaskCompletionSource(token);
                }
            }
        }, token)).ToArray();

        // When any of the worker is finished, we consider the whole query as done.
        // This prevent this operation from hanging on a timed out request
        await Task.WhenAny(worker);
        finished = true;
        await cts.CancelAsync();

        return CompileResult();

        async Task<(TNode target, TNode[]? retVal)> WrappedFindNeighbourOp(TNode node)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(_findNeighbourHardTimeout);

            try
            {
                // targetHash is implied in findNeighbourOp
                var ret = await findNeighbourOp(node, cts.Token);
                nodeHealthTracker.OnIncomingMessageFrom(node);

                return (node, ret);
            }
            catch (OperationCanceledException)
            {
                nodeHealthTracker.OnRequestFailed(node);
                return (node, null);
            }
            catch (Exception e)
            {
                nodeHealthTracker.OnRequestFailed(node);
                if (_logger.IsWarn) _logger.Warn($"Find neighbour op failed. {e}");
                if (_logger.IsDebug) _logger.Debug($"Find neighbour op failed. {e}");
                return (node, null);
            }
        }

        bool TryGetNodeToQuery([NotNullWhen(true)] out (ValueHash256, TNode)? toQuery)
        {
            using McsLock.Disposable _ = queueLock.Acquire();
            if (bestSeen.Count == 0)
            {
                toQuery = default;
                // No more node to query.
                // Note: its possible that there are other worker currently which may add to bestSeen.
                return false;
            }

            Interlocked.Increment(ref queryingTask);
            toQuery = bestSeen.Dequeue();
            return true;
        }

        void ProcessResult(ValueHash256 hash, TNode toQuery, (TNode, TNode[]? neighbours)? valueTuple, int round)
        {
            using var _ = queueLock.Acquire();

            finalResult.Enqueue((hash, toQuery), hash);
            while (finalResult.Count > k)
            {
                finalResult.Dequeue();
            }

            TNode[]? neighbours = valueTuple?.neighbours;
            if (neighbours == null) return;

            foreach (TNode neighbour in neighbours)
            {
                ValueHash256 neighbourHash = nodeHashProvider.GetHash(neighbour);

                // Already queried, we ignore
                if (queried.ContainsKey(neighbourHash)) continue;

                // When seen already dont record
                if (!seen.TryAdd(neighbourHash, neighbour)) continue;

                bestSeen.Enqueue((neighbourHash, neighbour), neighbourHash);

                if (closestNodeRound < round)
                {
                    if (finalResult.Count < k)
                    {
                        closestNodeRound = round;
                    }

                    // If the worst item in final result is worst that this neighbour, update closes node round
                    if (finalResult.TryPeek(out (ValueHash256 hash, TNode node) worstResult, out ValueHash256 _) && comparer.Compare(neighbourHash, worstResult.hash) < 0)
                    {
                        closestNodeRound = round;
                    }
                }
            }
        }

        TNode[] CompileResult()
        {
            using var _ = queueLock.Acquire();
            if (finalResult.Count > k) finalResult.Dequeue();
            return finalResult.UnorderedItems.Select((kv) => kv.Element.Item2).ToArray();
        }

        bool ShouldStopDueToNoBetterResult(out int round)
        {
            using var _ = queueLock.Acquire();

            round = Interlocked.Increment(ref currentRound);
            if (finalResult.Count >= k && round - closestNodeRound >= (config.Alpha * 2))
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
