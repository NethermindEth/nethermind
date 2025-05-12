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
        McsLock queueLock = new McsLock();
        PriorityQueue<(ValueHash256, TNode), ValueHash256> queryQueue = new(comparer);

        Channel<TNode> outChan = Channel.CreateBounded<TNode>(1);

        // Used for fast worker wake up when queue is empty
        TaskCompletionSource roundComplete = new TaskCompletionSource(token);
        int queryingTask = 0;

        // Used to determine if the worker should stop
        ValueHash256 bestNodeId = ValueKeccak.Zero;
        int closestNodeRound = 0;
        int currentRound = 0;
        int totalResult = 0;
        bool finished = false;

        // Check internal table first
        foreach (TNode node in routingTable.GetKNearestNeighbour(targetHash, null))
        {
            ValueHash256 nodeHash = nodeHashProvider.GetHash(node);
            seen.TryAdd(nodeHash, node);

            if (nodeHash == _currentNodeIdAsHash) continue;
            queryQueue.Enqueue((nodeHash, node), nodeHash);

            yield return node;

            if (bestNodeId == ValueKeccak.Zero || comparer.Compare(nodeHash, bestNodeId) < 0)
            {
                bestNodeId = nodeHash;
            }
        }

        Task[] worker = Enumerable.Range(0, config.Alpha).Select((i) => Task.Run(async () =>
        {
            var writer = outChan.Writer;
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

                Interlocked.Increment(ref queryingTask);
                try
                {
                    queried.TryAdd(toQuery.Value.hash, toQuery.Value.node);
                    if (_logger.IsTrace) _logger.Trace($"Query {toQuery.Value.node} at round {currentRound}, isself {SameAsSelf(toQuery.Value.node)}");
                    TNode[]? neighbours = await WrappedFindNeighbourOp(toQuery.Value.node);
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

                        Interlocked.Increment(ref minResult);
                        await writer.WriteAsync(neighbour, cts.Token);

                        using McsLock.Disposable _ = queueLock.Acquire();
                        bool foundBetter = comparer.Compare(neighbourHash, bestNodeId) < 0;
                        queryQueue.Enqueue((neighbourHash, neighbour), neighbourHash);

                        // If found a better node, reset closes node round.
                        // This causes `ShouldStopDueToNoBetterResult` to return false.
                        if (closestNodeRound < currentRound && foundBetter)
                        {
                            if (_logger.IsTrace) _logger.Trace($"Found better neighbour {neighbour} at round {currentRound}.");
                            bestNodeId = neighbourHash;
                            closestNodeRound = currentRound;
                        }
                    }
                    if (_logger.IsTrace) _logger.Trace($"Count {neighbours.Length}, queried {queryIgnored}, seen {seenIgnored}");

                    if (ShouldStopDueToNoBetterResult())
                    {
                        if (_logger.IsTrace) _logger.Trace("Stopping lookup. No better result.");
                        break;
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref queryingTask);
                    if (roundComplete.TrySetResult()) roundComplete = new TaskCompletionSource(token);
                }
            }

            outChan.Writer.TryComplete();
        }, token)).ToArray();

        await foreach (var node in outChan.Reader.ReadAllAsync(token))
        {
            yield return node;
        }

        // When any of the worker is finished, we consider the whole query as done.
        // This prevent this operation from hanging on a timed out request
        await Task.WhenAny(worker);
        finished = true;

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

        bool TryGetNodeToQuery([NotNullWhen(true)] out (ValueHash256, TNode)? toQuery)
        {
            using McsLock.Disposable _ = queueLock.Acquire();
            if (queryQueue.Count == 0)
            {
                toQuery = default;
                // No more node to query.
                // Note: its possible that there are other worker currently which may add to bestSeen.
                return false;
            }

            toQuery = queryQueue.Dequeue();
            return true;
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
