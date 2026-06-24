// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Nethermind.Logging;

namespace Nethermind.Kademlia;

/// <summary>
/// This find nearest k query does not follow the kademlia paper faithfully. Instead of distinct rounds, it has
/// num worker where alpha is the number of worker. Worker does not wait for other worker. Stop condition
/// happens if no more node to query or no new node can be added to the current result set that can improve it
/// for more than alpha*2 request. It is slightly faster than the legacy query on find value where it can be cancelled
/// earlier as it converge to the content faster, but take more query for findnodes due to a more strict stop
/// condition.
/// </summary>
public class LookupKNearestNeighbour<TKey, TNode, TKadKey>(
    IRoutingTable<TNode, TKadKey> routingTable,
    INodeHashProvider<TNode, TKadKey> nodeHashProvider,
    IKademliaDistance<TKadKey> distance,
    INodeHealthTracker<TNode> nodeHealthTracker,
    KademliaConfig<TNode> config,
    ILogManager? logManager = null) : ILookupAlgo<TNode, TKadKey>
    where TNode : notnull
    where TKadKey : notnull
{
    private readonly TimeSpan _findNeighbourHardTimeout = config.LookupFindNeighbourHardTimeout;
    private readonly ILogger _logger = (logManager ?? NullLogManager.Instance).GetClassLogger<LookupKNearestNeighbour<TKey, TNode, TKadKey>>();

    public async Task<TNode[]> Lookup(
        TKadKey targetHash,
        int k,
        Func<TNode, CancellationToken, Task<TNode[]?>> findNeighbourOp,
        CancellationToken token
    )
        => await LookupCore(targetHash, k, findNeighbourOp, null, token);

    public async IAsyncEnumerable<TNode> LookupNodes(
        TKadKey targetHash,
        int maxResults,
        Func<TNode, CancellationToken, Task<TNode[]?>> findNeighbourOp,
        [EnumeratorCancellation] CancellationToken token
    )
    {
        if (maxResults <= 0)
        {
            yield break;
        }

        Channel<TNode> results = Channel.CreateUnbounded<TNode>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        int emitted = 0;
        Task producer = ProduceResults();

        try
        {
            await foreach (TNode node in results.Reader.ReadAllAsync(token))
            {
                yield return node;
            }

            await producer;
        }
        finally
        {
            await cts.CancelAsync();
            try
            {
                await producer;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
        }

        async Task ProduceResults()
        {
            Exception? error = null;
            try
            {
                _ = await LookupCore(targetHash, maxResults, findNeighbourOp, Publish, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                error = e;
            }
            finally
            {
                results.Writer.TryComplete(error);
            }
        }

        bool Publish(TNode node)
        {
            int count = Interlocked.Increment(ref emitted);
            if (count <= maxResults)
            {
                results.Writer.TryWrite(node);
            }

            if (count >= maxResults)
            {
                cts.Cancel();
                return false;
            }

            return true;
        }
    }

    private async Task<TNode[]> LookupCore(
        TKadKey targetHash,
        int k,
        Func<TNode, CancellationToken, Task<TNode[]?>> findNeighbourOp,
        Func<TNode, bool>? publishNode,
        CancellationToken token
    )
    {
        if (_logger.IsDebug)
        {
            _logger.Debug($"Initiate lookup for hash {targetHash}");
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = cts.Token;

        ConcurrentDictionary<TKadKey, TNode> queried = new();
        ConcurrentDictionary<TKadKey, TNode> seen = new();

        IComparer<TKadKey> comparer = Comparer<TKadKey>.Create((h1, h2) =>
            distance.Compare(h1, h2, targetHash));
        IComparer<TKadKey> comparerReverse = Comparer<TKadKey>.Create((h1, h2) =>
            distance.Compare(h2, h1, targetHash));

        Lock queueLock = new();

        // Ordered by lowest distance. Will get popped for next round.
        PriorityQueue<(TKadKey, TNode), TKadKey> bestSeen = new(comparer);

        // Ordered by highest distance. Added on result. Get popped as result.
        PriorityQueue<(TKadKey, TNode), TKadKey> finalResult = new(comparerReverse);

        TaskCompletionSource roundComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int closestNodeRound = 0;
        int currentRound = 0;
        int queryingTask = 0;
        bool finished = false;

        foreach (TNode node in routingTable.GetKNearestNeighbour(targetHash))
        {
            TKadKey nodeHash = nodeHashProvider.GetHash(node);
            if (!seen.TryAdd(nodeHash, node))
            {
                continue;
            }

            bestSeen.Enqueue((nodeHash, node), nodeHash);
            if (!TryPublish(node))
            {
                Volatile.Write(ref finished, true);
                break;
            }
        }

        Task[] workers = new Task[config.Alpha];
        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                while (!Volatile.Read(ref finished))
                {
                    token.ThrowIfCancellationRequested();
                    if (!TryGetNodeToQuery(out TKadKey toQueryHash, out TNode toQueryNode))
                    {
                        if (queryingTask > 0)
                        {
                            // Need to wait for all querying tasks first here.
                            await Task.WhenAny(Volatile.Read(ref roundComplete).Task, Task.Delay(100, token));
                            continue;
                        }

                        // No node to query and running query.
                        if (_logger.IsTrace) _logger.Trace("Stopping lookup. No node to query.");
                        break;
                    }

                    try
                    {
                        if (ShouldStopDueToNoBetterResult(out int round))
                        {
                            if (_logger.IsTrace) _logger.Trace("Stopping lookup. No better result.");
                            break;
                        }

                        queried.TryAdd(toQueryHash, toQueryNode);
                        TNode[]? neighbours = await WrappedFindNeighbourOp(toQueryNode);
                        if (neighbours is null) continue;

                        ProcessResult(toQueryHash, toQueryNode, neighbours, round);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref queryingTask);
                        TaskCompletionSource current = Volatile.Read(ref roundComplete);
                        if (current.TrySetResult())
                        {
                            Interlocked.CompareExchange(
                                ref roundComplete,
                                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                                current);
                        }
                    }
                }
            }, token);
        }

        // When any of the worker is finished, we consider the whole query as done.
        // This prevent this operation from hanging on a timed out request
        await Task.WhenAny(workers);
        Volatile.Write(ref finished, true);
        await cts.CancelAsync();
        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }

        return publishNode is null ? CompileResult() : [];

        async Task<TNode[]?> WrappedFindNeighbourOp(TNode node)
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(_findNeighbourHardTimeout);

            try
            {
                // targetHash is implied in findNeighbourOp
                TNode[]? ret = await findNeighbourOp(node, cts.Token);
                if (ret is null) return null;

                nodeHealthTracker.OnIncomingMessageFrom(node);

                return ret;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                nodeHealthTracker.OnRequestFailed(node);
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                nodeHealthTracker.OnRequestFailed(node);
                if (_logger.IsWarn) _logger.Warn($"Find neighbour op failed: {e}");
                return null;
            }
        }

        bool TryGetNodeToQuery(out TKadKey hash, out TNode node)
        {
            lock (queueLock)
            {
                if (bestSeen.Count == 0)
                {
                    hash = default!;
                    node = default!;
                    // No more node to query.
                    // Note: its possible that there are other worker currently which may add to bestSeen.
                    return false;
                }

                Interlocked.Increment(ref queryingTask);
                (hash, node) = bestSeen.Dequeue();
                return true;
            }
        }

        void ProcessResult(TKadKey hash, TNode toQuery, TNode[] neighbours, int round)
        {
            lock (queueLock)
            {
                finalResult.Enqueue((hash, toQuery), hash);
                while (finalResult.Count > k)
                {
                    finalResult.Dequeue();
                }

                foreach (TNode neighbour in neighbours)
                {
                    TKadKey neighbourHash = nodeHashProvider.GetHash(neighbour);

                    // Already queried, we ignore
                    if (queried.ContainsKey(neighbourHash)) continue;

                    // When seen already dont record
                    if (!seen.TryAdd(neighbourHash, neighbour)) continue;

                    bestSeen.Enqueue((neighbourHash, neighbour), neighbourHash);
                    if (!TryPublish(neighbour))
                    {
                        Volatile.Write(ref finished, true);
                        break;
                    }

                    if (closestNodeRound < round)
                    {
                        if (finalResult.Count < k)
                        {
                            closestNodeRound = round;
                        }

                        // If the worst item in final result is worst that this neighbour, update closes node round
                        if (finalResult.TryPeek(out (TKadKey hash, TNode node) worstResult, out _) && comparer.Compare(neighbourHash, worstResult.hash) < 0)
                        {
                            closestNodeRound = round;
                        }
                    }
                }
            }
        }

        TNode[] CompileResult()
        {
            lock (queueLock)
            {
                if (finalResult.Count > k) finalResult.Dequeue();
                TNode[] result = new TNode[finalResult.Count];
                int i = 0;
                foreach (((TKadKey, TNode) Element, TKadKey Priority) entry in finalResult.UnorderedItems)
                {
                    result[i++] = entry.Element.Item2;
                }

                return result;
            }
        }

        bool ShouldStopDueToNoBetterResult(out int round)
        {
            lock (queueLock)
            {
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

        bool TryPublish(TNode node) => publishNode?.Invoke(node) ?? true;
    }
}
