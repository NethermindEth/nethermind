// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Logging;
using NonBlocking;

namespace Nethermind.Network.Discovery.Kademlia;

/// Single array of kbucket kademlia implementation.
/// Not even the splitting variant.
/// With a proper splitting variant, the closest kbucket will be full and less sparse, so the findNeighbour query
/// is more accurate without having to spill over to other kbucket to fill the query.
/// This is even more so with tree based kbucket where bucket without currentid can also be splitted down (to a predefined
/// limit) which makes the lookup even more accurate.
///
/// TODO: Switch to tree based kademlia implementation.
public class Kademlia<TNode, TContentKey, TContent> : IKademlia<TNode, TContentKey, TContent> where TNode : notnull
{
    private static readonly TimeSpan FindNeighbourHardTimeout = TimeSpan.FromSeconds(5);

    private readonly IKademlia<TNode, TContentKey, TContent>.IStore _store;
    private readonly INodeHashProvider<TNode, TContentKey> _nodeHashProvider;
    private readonly ConcurrentDictionary<ValueHash256, bool> _isRefreshing = new();

    private readonly KBucket<TNode>[] _buckets;
    private readonly TNode _currentNodeId;
    private readonly ValueHash256 _currentNodeIdAsHash;
    private readonly int _kSize;
    private readonly int _alpha;
    private readonly bool _useNewLookup = true;
    private readonly IMessageSender<TNode, TContentKey, TContent> _messageSender;
    private readonly LruCache<ValueHash256, int> _peerFailures;
    private readonly TimeSpan _refreshInterval;
    private readonly ILogger _logger;

    public Kademlia(
        INodeHashProvider<TNode, TContentKey> nodeHashProvider,
        IKademlia<TNode, TContentKey, TContent>.IStore store,
        IMessageSender<TNode, TContentKey, TContent> sender,
        ILogManager logManager,
        TNode currentNodeId,
        int kSize,
        int alpha,
        TimeSpan refreshInterval,
        bool useNewLookup = true
    )
    {
        _nodeHashProvider = nodeHashProvider;
        _store = store;
        _messageSender = new MessageSenderMonitor(sender, this);
        _logger = logManager.GetClassLogger<Kademlia<TNode, TContentKey, TContent>>();

        _currentNodeId = currentNodeId;
        _currentNodeIdAsHash = _nodeHashProvider.GetHash(_currentNodeId);
        _kSize = kSize;
        _alpha = alpha;
        _refreshInterval = refreshInterval;

        _peerFailures = new LruCache<ValueHash256, int>(1024, "peer failure");
        // Note: It does not have to be this much. In practice, only like 16 of these bucket get populated.
        _buckets = new KBucket<TNode>[Hash256XORUtils.MaxDistance + 1];
        for (int i = 0; i < Hash256XORUtils.MaxDistance + 1; i++)
        {
            _buckets[i] = new KBucket<TNode>(kSize);
        }

        _useNewLookup = useNewLookup;
    }

    public void AddOrRefresh(TNode node)
    {
        if (SameAsSelf(node)) return;

        _isRefreshing.TryRemove(_nodeHashProvider.GetHash(node), out _);

        var bucket = GetBucket(node);
        if (!bucket.TryAddOrRefresh(_nodeHashProvider.GetHash(node), node, out TNode? toRefresh))
        {
            if (toRefresh != null) TryRefresh(toRefresh);
        }
    }

    private void TryRefresh(TNode toRefresh)
    {
        ValueHash256 nodeHash = _nodeHashProvider.GetHash(toRefresh);
        if (_isRefreshing.TryAdd(nodeHash, true))
        {
            Task.Run(async () =>
            {
                // First, we delay in case any new message come and clear the refresh task, so we don't send any ping.
                await Task.Delay(100);
                if (!_isRefreshing.ContainsKey(nodeHash))
                {
                    return;
                }

                // OK, fine, we'll ping it.
                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                try
                {
                    await _messageSender.Ping(toRefresh, cts.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e)
                {
                    if (_logger.IsDebug) _logger.Debug($"Error while refreshing node {toRefresh}, {e}");
                }

                // In any case, if a pong happened, AddOrRefresh would have been called and _isRefreshing would
                // remove the entry.
                if (_isRefreshing.TryRemove(nodeHash, out _))
                {
                    // Well... basically its not responding.
                    GetBucket(toRefresh).RemoveAndReplace(nodeHash);
                }
            });
        }
    }

    public TNode[] GetAllAtDistance(int i)
    {
        return _buckets[i].GetAll();
    }

    private bool SameAsSelf(TNode node)
    {
        // TODO: Put in distance calculator.. probably
        return EqualityComparer<TNode>.Default.Equals(node, _currentNodeId);
    }

    public async Task<TContent?> LookupValue(TContentKey contentKey, CancellationToken token)
    {
        TContent? result = default(TContent);
        bool resultWasFound = false;

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = cts.Token;
        // TODO: Timeout?

        ValueHash256 targetHash = _nodeHashProvider.GetHash(contentKey);

        try
        {
            await LookupNodesClosest(
                targetHash, _kSize, async (nextNode, token) =>
                {
                    FindValueResponse<TNode, TContent> valueResponse = await _messageSender.FindValue(nextNode, contentKey, token);
                    if (valueResponse.hasValue)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Value response has value {valueResponse.value}");
                        resultWasFound = true;
                        result = valueResponse.value; // Shortcut so that once it find the value, it should stop.
                        await cts.CancelAsync();
                    }

                    if (_logger.IsDebug) _logger.Debug($"Value response has no value. Returning {valueResponse.neighbours.Length} neighbours");
                    return valueResponse.neighbours;
                },
                token
            );
        }
        catch (OperationCanceledException)
        {
            if (!resultWasFound) throw;
        }

        return result;
    }

    public async Task<TNode[]> LookupNodesClosest(ValueHash256 targetHash, int k, CancellationToken token)
    {
        return await LookupNodesClosest(
            targetHash,
            k,
            async (nextNode, token) =>
            {
                _logger.Warn($"Lookup node closes {nextNode}");
                return await _messageSender.FindNeighbours(nextNode, targetHash, token);
            },
            token
        );
    }

    /// <summary>
    /// Main find closest-k node within the network. See the kademlia paper, 2.3.
    /// Since find value is basically the same also just with a shortcut, this allow changing the find neighbour op.
    /// Find closest-k is also used to determine which node should store a particular value which is used by
    /// store RPC (not implemented).
    /// </summary>
    /// <param name="targetHash"></param>
    /// <param name="k"></param>
    /// <param name="findNeighbourOp"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    private Task<TNode[]> LookupNodesClosest(
        ValueHash256 targetHash,
        int k,
        Func<TNode, CancellationToken, Task<TNode[]?>> findNeighbourOp,
        CancellationToken token
    )
    {
        if (_useNewLookup)
        {
            return LookupNodesClosestNew(
                targetHash,
                k,
                findNeighbourOp,
                token
            );
        }

        return LookupNodesClosestLegacy(
            targetHash,
            k,
            findNeighbourOp,
            token
        );
    }

    /// <summary>
    /// This find nearest k query does not follow the kademlia paper faithfully. Instead of distinct rounds, it has
    /// num worker where alpha is the number of worker. Worker does not wait for other worker. Stop condition
    /// happens if no more node to query or no new node can be added to the current result set that can improve it
    /// for more than alpha*2 request. It is slightly faster than the legacy query on find value where it can be cancelled
    /// earlier as it converge to the content faster, but take more query for findnodes due to a more strict stop
    /// condition.
    /// </summary>
    /// <param name="targetHash"></param>
    /// <param name="k"></param>
    /// <param name="findNeighbourOp"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    private async Task<TNode[]> LookupNodesClosestNew(
        ValueHash256 targetHash,
        int k,
        Func<TNode, CancellationToken, Task<TNode[]?>> findNeighbourOp,
        CancellationToken token
    ) {
        if (_logger.IsDebug) _logger.Debug($"Initiate lookup for hash {targetHash}");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = cts.Token;

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

        ConcurrentDictionary<ValueHash256, TNode> queried = new();
        ConcurrentDictionary<ValueHash256, TNode> seen = new();

        IComparer<ValueHash256> comparer = Comparer<ValueHash256>.Create((h1, h2) =>
            Hash256XORUtils.Compare(h1, h2, targetHash));
        IComparer<ValueHash256> comparerReverse = Comparer<ValueHash256>.Create((h1, h2) =>
            Hash256XORUtils.Compare(h2, h1, targetHash));

        McsLock queueLock = new McsLock();

        // Ordered by lowest distance. Will get popped for next round.
        PriorityQueue<(ValueHash256, TNode), ValueHash256> bestSeen = new(comparer);

        // Ordered by highest distance. Added on result. Get popped as result.
        PriorityQueue<(ValueHash256, TNode), ValueHash256> finalResult = new(comparerReverse);

        foreach (TNode node in IterateNeighbour(targetHash).Take(_kSize))
        {
            ValueHash256 nodeHash = _nodeHashProvider.GetHash(node);
            seen.TryAdd(nodeHash, node);
            bestSeen.Enqueue((nodeHash, node), nodeHash);
        }

        TaskCompletionSource roundComplete = new TaskCompletionSource(token);
        int closestNodeRound = 0;
        int currentRound = 0;
        int queryingTask = 0;
        bool finished = false;

        Task[] worker = Enumerable.Range(0, _alpha).Select((i) => Task.Run(async () =>
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
                    (TNode, TNode[]? neighbours)? result = await wrappedFindNeighbourHop(toQuery.Value.node);
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
                if (SameAsSelf(neighbour)) continue;

                ValueHash256 neighbourHash = _nodeHashProvider.GetHash(neighbour);

                // Already queried, we ignore
                if (queried.ContainsKey(neighbourHash)) continue;

                // When seen already dont record
                if (!seen.TryAdd(neighbourHash, neighbour)) continue;

                bestSeen.Enqueue((neighbourHash, neighbour), neighbourHash);

                if (closestNodeRound < round)
                {
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
            if (finalResult.Count >= k && round - closestNodeRound >= (_alpha*2))
            {
                // No closer node for more than or equal to _alpha*2 round.
                // Assume exit condition
                // Why not just _alpha?
                // Because there could be currently running work that may increase closestNodeRound.
                // So including this worker, assume no more
                if (_logger.IsTrace) _logger.Trace("No more closer node");
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// This find nearest k query follows the kademlia paper faithfully, but does not do much parallelism.
    /// </summary>
    /// <param name="targetHash"></param>
    /// <param name="k"></param>
    /// <param name="findNeighbourOp"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    private async Task<TNode[]> LookupNodesClosestLegacy(
        ValueHash256 targetHash,
        int k,
        Func<TNode, CancellationToken, Task<TNode[]?>> findNeighbourOp,
        CancellationToken token
    ) {
        if (_logger.IsDebug) _logger.Debug($"Initiate lookup for hash {targetHash}");

        Func<TNode, Task<(TNode target, TNode[]? retVal)>> wrappedFindNeighbourHop = async (node) =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            // cts.CancelAfter(FindNeighbourHardTimeout);

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

        ValueHash256 closestNodeHash = _nodeHashProvider.GetHash(_currentNodeId);
        (ValueHash256 nodeHash, TNode node)[] roundQuery = IterateNeighbour(targetHash)
            .Take(_alpha)
            .Select((node) => (_nodeHashProvider.GetHash(node), node))
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

                queriedAndResponded.TryAdd(_nodeHashProvider.GetHash(response.NodeId), response.NodeId);

                foreach (TNode neighbour in response.Neighbours)
                {
                    if (SameAsSelf(neighbour)) continue;

                    ValueHash256 neighbourHash = _nodeHashProvider.GetHash(neighbour);
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

            int toTake = Math.Min(_alpha, bestSeen.Count);
            roundQuery = Enumerable.Range(0, toTake).Select((_) =>
            {
                TNode node = bestSeen.Dequeue();
                return (_nodeHashProvider.GetHash(node), node);
            }).ToArray();
        }

        // At this point need to query for the maxNode.
        List<TNode> result = [];
        while (result.Count < k && bestSeenAllTime.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            TNode nextLowest = bestSeenAllTime.Dequeue();
            ValueHash256 nextLowestHash = _nodeHashProvider.GetHash(nextLowest);

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

    public async Task Run(CancellationToken token)
    {
        await LookupNodesClosest(_currentNodeIdAsHash, _kSize, token);

        while (true)
        {
            await Bootstrap(token);

            await Task.Delay(_refreshInterval, token);
        }
    }

    public async Task Bootstrap(CancellationToken token)
    {
        Stopwatch sw = Stopwatch.StartNew();
        await LookupNodesClosest(_currentNodeIdAsHash, _kSize, token);

        token.ThrowIfCancellationRequested();

        // Refreshes all bucket. one by one. That is not empty.
        // A refresh means to do a k-nearest node lookup for a random hash for that particular bucket.
        for (var i = 0; i < _buckets.Length; i++)
        {
            token.ThrowIfCancellationRequested();

            if (_buckets[i].Count > 0)
            {
                ValueHash256 nodeToLookup = Hash256XORUtils.GetRandomHashAtDistance(_currentNodeIdAsHash, i);
                await LookupNodesClosest(nodeToLookup, _kSize, token);
            }
        }

        _logger.Info($"Boostrap completed. Took {sw}. Bucket sizes (from 230) {string.Join(",", _buckets[200..].Select((b) => b.Count).ToList())}");
    }

    public IEnumerable<TNode> IterateNeighbour(ValueHash256 hash)
    {
        int startingDistance = Hash256XORUtils.CalculateDistance(_currentNodeIdAsHash, hash);
        foreach (var bucketToGet in EnumerateBucket(startingDistance))
        {
            foreach (TNode bucketContent in _buckets[bucketToGet].GetAll())
            {
                yield return bucketContent;
            }
        }
    }

    private IEnumerable<int> EnumerateBucket(int startingDistance)
    {
        // Note, without a tree based routing table, we don't exactly know
        // which way (left or right) is the right way to go. So this is all approximate.
        // Well, even with a full tree, it would still be approximate, just that it would
        // be a bit more accurate.
        yield return startingDistance;
        int left = startingDistance - 1;
        int right = startingDistance + 1;
        while (left > 0 || right <= Hash256XORUtils.MaxDistance)
        {
            if (left > 0)
            {
                yield return left;
            }

            if (right <= Hash256XORUtils.MaxDistance)
            {
                yield return right;
            }

            left -= 1;
            right += 1;
        }
    }

    private KBucket<TNode> GetBucket(TNode node)
    {
        int idx = Hash256XORUtils.CalculateDistance(_nodeHashProvider.GetHash(node), _currentNodeIdAsHash);
        return _buckets[idx];
    }

    private void OnIncomingMessageFrom(TNode sender)
    {
        AddOrRefresh(sender);
        _peerFailures.Delete(_nodeHashProvider.GetHash(sender));
    }

    private void OnRequestFailed(TNode receiver)
    {
        ValueHash256 hash = _nodeHashProvider.GetHash(receiver);
        if (!_peerFailures.TryGet(hash, out var currentFailure))
        {
            _peerFailures.Set(hash, 1);
            return;
        }

        if (currentFailure >= 5)
        {
            GetBucket(receiver).Remove(hash);
            _peerFailures.Delete(hash);

        }

        _peerFailures.Set(hash, currentFailure + 1);
    }

    public Task Ping(TNode sender, CancellationToken token)
    {
        OnIncomingMessageFrom(sender);
        return Task.CompletedTask;
    }

    public Task<TNode[]> FindNeighbours(TNode sender, ValueHash256 hash, CancellationToken token)
    {
        OnIncomingMessageFrom(sender);
        return Task.FromResult(IterateNeighbour(hash).Take(_kSize).ToArray());
    }

    public Task<FindValueResponse<TNode, TContent>> FindValue(TNode sender, TContentKey contentKey, CancellationToken token)
    {
        OnIncomingMessageFrom(sender);

        if (_store.TryGetValue(contentKey, out TContent? value))
        {
            return Task.FromResult(new FindValueResponse<TNode, TContent>(true, value!, Array.Empty<TNode>()));
        }

        return Task.FromResult(
            new FindValueResponse<TNode, TContent>(
                false,
                default,
                IterateNeighbour(_nodeHashProvider.GetHash(contentKey)).Take(_kSize).ToArray() // TODO: pass an n so that its possible to skip creating array
            ));
    }

    /// <summary>
    /// Monitor requests for success or failure.
    /// </summary>
    /// <param name="implementation"></param>
    /// <param name="kademlia"></param>
    private class MessageSenderMonitor(IMessageSender<TNode, TContentKey, TContent> implementation, Kademlia<TNode, TContentKey, TContent> kademlia) : IMessageSender<TNode, TContentKey, TContent>
    {
        public async Task Ping(TNode receiver, CancellationToken token)
        {
            try
            {
                await implementation.Ping(receiver, token);
                kademlia.OnIncomingMessageFrom(receiver);
            }
            catch (OperationCanceledException)
            {
                kademlia.OnRequestFailed(receiver);
                throw;
            }
        }

        public async Task<TNode[]> FindNeighbours(TNode receiver, ValueHash256 hash, CancellationToken token)
        {
            try
            {
                TNode[] res = await implementation.FindNeighbours(receiver, hash, token);
                kademlia.OnIncomingMessageFrom(receiver);
                return res;
            }
            catch (OperationCanceledException)
            {
                kademlia.OnRequestFailed(receiver);
                throw;
            }
        }

        public Task<FindValueResponse<TNode, TContent>> FindValue(TNode receiver, TContentKey contentKey, CancellationToken token)
        {
            try
            {
                Task<FindValueResponse<TNode, TContent>> res = implementation.FindValue(receiver, contentKey, token);
                kademlia.OnIncomingMessageFrom(receiver);
                return res;
            }
            catch (OperationCanceledException)
            {
                kademlia.OnRequestFailed(receiver);
                throw;
            }
        }
    }
}
