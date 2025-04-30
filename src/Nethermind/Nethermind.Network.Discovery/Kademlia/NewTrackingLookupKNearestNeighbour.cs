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

public class NewaTrackingLookupKNearestNeighbour<TKey, TNode>(
    IRoutingTable<TNode> routingTable,
    INodeHashProvider<TNode> nodeHashProvider,
    IKeyOperator<TKey, TNode> keyOperator,
    KademliaConfig<TNode> kademliaConfig,
    IKademliaMessageSender<TKey, TNode> kademliaMessageSender,
    INodeHealthTracker<TNode> nodeHealthTracker,
    KademliaConfig<TNode> config,
    ILogManager logManager) : ILookupAlgo2<TKey, TNode> where TNode : notnull
{
    private readonly TimeSpan _findNeighbourHardTimeout = config.LookupFindNeighbourHardTimout;
    private readonly ILogger _logger = logManager.GetClassLogger<NewaTrackingLookupKNearestNeighbour<TKey, TNode>>();
    private readonly ValueHash256 _currentNodeIdAsHash = nodeHashProvider.GetHash(kademliaConfig.CurrentNodeId);

    private async Task<TNode[]> LookupFunc(TNode nextNode, TKey target, CancellationToken token)
    {
        if (SameAsSelf(nextNode))
        {
            return routingTable.GetKNearestNeighbour(keyOperator.GetKeyHash(target));
        }
        return await kademliaMessageSender.FindNeighbours(nextNode, target, token);
    }

    private bool SameAsSelf(TNode node)
    {
        return nodeHashProvider.GetHash(node) == _currentNodeIdAsHash;
    }

    public async IAsyncEnumerable<TNode> Lookup(
        TKey target,
        [EnumeratorCancellation] CancellationToken token
    ) {
        if (_logger.IsDebug) _logger.Debug($"Initiate lookup for hash {target}");

        using var cts = token.CreateChildTokenSource();
        token = cts.Token;

        var targetHash = keyOperator.GetKeyHash(target);
        ConcurrentDictionary<ValueHash256, TNode> queried = new();
        ConcurrentDictionary<ValueHash256, TNode> seen = new();

        IComparer<ValueHash256> comparer = Comparer<ValueHash256>.Create((h1, h2) =>
            Hash256XorUtils.Compare(h1, h2, targetHash));

        McsLock queueLock = new McsLock();

        // Ordered by lowest distance. Will get popped for next round.
        PriorityQueue<(ValueHash256, TNode), ValueHash256> queryQueue = new(comparer);
        ValueHash256 bestNodeId = ValueKeccak.Zero;

        foreach (TNode node in routingTable.GetKNearestNeighbour(targetHash, default))
        {
            ValueHash256 nodeHash = nodeHashProvider.GetHash(node);
            seen.TryAdd(nodeHash, node);
            queryQueue.Enqueue((nodeHash, node), nodeHash);

            if (bestNodeId == ValueKeccak.Zero || comparer.Compare(nodeHash, bestNodeId) < 0)
            {
                bestNodeId = nodeHash;
            }
        }

        Channel<TNode> outChan = Channel.CreateBounded<TNode>(1);

        TaskCompletionSource roundComplete = new TaskCompletionSource(token);
        int closestNodeRound = 0;
        int currentRound = 0;
        int queryingTask = 0;
        int minResult = 128;
        int totalResult = 0;
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
                    _logger.Warn("Stopping lookup. No node to query.");
                    break;
                }

                Interlocked.Increment(ref queryingTask);
                try
                {
                    queried.TryAdd(toQuery.Value.hash, toQuery.Value.node);
                    if (_logger.IsTrace) _logger.Trace($"Query {toQuery.Value.node} at round {currentRound}, isself {SameAsSelf(toQuery.Value.node)}");
                    (TNode, TNode[]? neighbours) result = await WrappedFindNeighbourOp(toQuery.Value.node);
                    if (result.neighbours == null || result.neighbours?.Length == 0)
                    {
                        if (_logger.IsTrace) _logger.Trace("Empty result");
                        continue;
                    }

                    await ProcessResult(toQuery.Value.node, result, currentRound);
                    if (ShouldStopDueToNoBetterResult(out var round))
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

        _logger.Warn("Lookup operation finished.");
        yield break;

        async Task<(TNode target, TNode[]? retVal)> WrappedFindNeighbourOp(TNode node)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(_findNeighbourHardTimeout);

            try
            {
                // targetHash is implied in findNeighbourOp
                var ret = await LookupFunc(node, target, cts.Token);
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
                if (_logger.IsWarn) _logger.Warn($"Find neighbour op failed. {e}");
                nodeHealthTracker.OnRequestFailed(node);
                if (_logger.IsDebug) _logger.Debug($"Find neighbour op failed. {e}");
                return (node, null);
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

        async Task ProcessResult(TNode thisNode, (TNode, TNode[]? neighbours)? valueTuple, int round)
        {
            TNode[]? neighbours = valueTuple?.neighbours;
            if (neighbours == null) return;

            var writer = outChan.Writer;
            int queryIgnored = 0;
            int seenIgnored = 0;
            foreach (TNode neighbour in neighbours)
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

                using var _ = queueLock.Acquire();
                bool foundBetter = comparer.Compare(neighbourHash, bestNodeId) < 0;
                queryQueue.Enqueue((neighbourHash, neighbour), neighbourHash);

                // If found a better node, reset closes node round and continue
                if (closestNodeRound < round && foundBetter)
                {
                    _logger.Warn($"Found better neighbour {neighbour} at round {round}.");
                    bestNodeId = neighbourHash;
                    closestNodeRound = round;
                }
            }
            _logger.Warn($"Count {neighbours.Length}, queried {queryIgnored}, seen {seenIgnored}");
        }

        bool ShouldStopDueToNoBetterResult(out int round)
        {
            round = Interlocked.Increment(ref currentRound);
            if (totalResult >= minResult && round - closestNodeRound >= (config.Alpha*2))
            {
                // No closer node for more than or equal to _alpha*2 round.
                // Assume exit condition
                // Why not just _alpha?
                // Because there could be currently running work that may increase closestNodeRound.
                // So including this worker, assume no more
                if (_logger.IsTrace) _logger.Trace($"No more closer node. Round: {round}, closestNodeRound {closestNodeRound}");
                _logger.Warn($"No more closer node. Round: {round}, closestNodeRound {closestNodeRound}");
                return true;
            }

            return false;
        }
    }
}
