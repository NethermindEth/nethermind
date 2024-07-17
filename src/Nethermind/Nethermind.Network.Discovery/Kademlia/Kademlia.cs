// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Diagnostics;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;

namespace Nethermind.Network.Discovery.Kademlia;

/// Single array of kbucket kademlia implementation.
/// Not even the splitting variant.
/// With a proper splitting variant, the closest kbucket will be full and less sparse, so the findNeighbour query
/// is more accurate without having to spill over to other kbucket to fill the query.
/// This is even more so with tree based kbucket where bucket without currentid can also be splitted down (to a predefined
/// limit) which makes the lookup even more accurate.
///
/// TODO: Switch to tree based kademlia implementation.
public class Kademlia<THash, TValue> : IKademlia<THash, TValue> where THash : notnull
{
    private IKademlia<THash, TValue>.IStore _store;
    private readonly IDistanceCalculator<THash> _distanceCalculator;

    private readonly KBucket<THash, TValue>[] _buckets;
    private readonly THash _currentNodeId;
    private readonly int _kSize;
    private readonly int _alpha;
    private readonly IMessageSender<THash, TValue> _messageSender;
    private readonly LruCache<THash, int> _peerFailures;
    private readonly TimeSpan _refreshInterval;

    public Kademlia(
        IDistanceCalculator<THash> distanceCalculator,
        IKademlia<THash, TValue>.IStore store,
        IMessageSender<THash, TValue> sender,
        THash currentNodeId,
        int kSize,
        int alpha,
        TimeSpan refreshInterval
    )
    {
        _distanceCalculator = distanceCalculator;
        _store = store;
        _messageSender = new MessageSenderMonitor(sender, this);

        _currentNodeId = currentNodeId;
        _kSize = kSize;
        _alpha = alpha;
        _refreshInterval = refreshInterval;

        _peerFailures = new LruCache<THash, int>(1024, "peer failure");
        // Note: It does not have to be this mush. In practice, only like 16 of these bucket get populated.
        _buckets = new KBucket<THash, TValue>[distanceCalculator.MaxDistance + 1];
        for (int i = 0; i < distanceCalculator.MaxDistance + 1; i++)
        {
            _buckets[i] = new KBucket<THash, TValue>(kSize, sender);
        }
    }

    public void SeedNode(THash node)
    {
        if (SameAsSelf(node)) return;
        GetBucket(node).AddOrRefresh(node);
    }

    private bool SameAsSelf(THash node)
    {
        // TODO: Put in distance calculator.. probably
        return EqualityComparer<THash>.Default.Equals(node, _currentNodeId);
    }

    public async Task<TValue?> LookupValue(THash targetHash, CancellationToken token)
    {
        TValue? result = default(TValue);
        bool resultWasFound = false;

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        // TODO: Timeout?

        try
        {
            await LookupNodesClosest(
                targetHash, async (nextNode, token) =>
                {
                    FindValueResponse<THash, TValue> valueResponse = await _messageSender.FindValue(nextNode, targetHash, token);
                    if (valueResponse.hasValue)
                    {
                        resultWasFound = true;
                        result = valueResponse.value; // Shortcut so that once it find the value, it should stop.
                        await cts.CancelAsync();
                    }

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

    private async Task<THash[]> LookupNodesClosest(THash targetHash, CancellationToken token)
    {
        return await LookupNodesClosest(
            targetHash,
            async (nextNode, token) => await _messageSender.FindNeighbours(nextNode, targetHash, token),
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
    /// <param name="findNeighbourOp"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    private async Task<THash[]> LookupNodesClosest(
        THash targetHash,
        Func<THash, CancellationToken, Task<THash[]?>> findNeighbourOp,
        CancellationToken token
    ) {
        HashSet<THash> queried = new HashSet<THash>();
        HashSet<THash> queriedAndResponded = new HashSet<THash>();
        HashSet<THash> seen = new HashSet<THash>();

        IComparer<THash> comparer = Comparer<THash>.Create((h1, h2) => _distanceCalculator.Compare(h1, h2, targetHash));

        // Ordered by lowest distance. Will get popped for next round.
        PriorityQueue<THash, THash> bestSeen = new PriorityQueue<THash, THash>(comparer);

        // Ordered by lowest distance. Will not get popped for next round, but will at final collection.
        PriorityQueue<THash, THash> bestSeenAllTime = new PriorityQueue<THash, THash>(comparer);

        THash closestNode = _currentNodeId;
        THash[] roundQuery = IterateNeighbour(targetHash).Take(_alpha).ToArray();
        foreach (THash hash in roundQuery)
        {
            seen.AddRange(hash);
            bestSeen.Enqueue(hash, hash);
            bestSeenAllTime.Enqueue(hash, hash);
        }

        while (roundQuery.Length > 0)
        {
            // TODO: The paper mentioned that the next round can start immediately while waiting
            // for the result of previous round.
            token.ThrowIfCancellationRequested();

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromMilliseconds(100));

            queried.AddRange(roundQuery);
            (THash NodeId, THash[]? Neighbours)[] currentRoundResponse = await Task.WhenAll(roundQuery.Select(async (hash) =>
            {
                try
                {
                    return (hash, await findNeighbourOp(hash, cts.Token));
                }
                catch (OperationCanceledException)
                {
                    return (hash, null);
                }
            }));

            bool hasCloserThanClosest = false;
            foreach ((THash NodeId, THash[]? Neighbours) response in currentRoundResponse)
            {
                if (response.Neighbours == null) continue; // Timeout or failed to get response

                queriedAndResponded.Add(response.NodeId);

                foreach (THash neighbour in response.Neighbours)
                {
                    if (SameAsSelf(neighbour)) continue;

                    // Already queried, we ignore
                    if (queried.Contains(neighbour)) continue;

                    // When seen already dont record
                    if (!seen.Add(neighbour)) continue;
                    bestSeen.Enqueue(neighbour, neighbour);
                    bestSeenAllTime.Enqueue(neighbour, neighbour);

                    if (comparer.Compare(neighbour, closestNode) < 0)
                    {
                        hasCloserThanClosest = true;
                        closestNode = neighbour;
                    }
                }
            }

            if (!hasCloserThanClosest)
            {
                // end condition it seems
                break;
            }

            int toTake = Math.Min(_alpha, bestSeen.Count);
            roundQuery = Enumerable.Range(0, toTake).Select((_) => bestSeen.Dequeue()).ToArray();
        }

        PrintDebug($"first phase done");

        // At this point need to query for the maxNode.
        List<THash> result = [];
        while (result.Count < _kSize && bestSeenAllTime.Count > 0)
        {
            THash nextLowest = bestSeenAllTime.Dequeue();
            if (queriedAndResponded.Contains(nextLowest))
            {
                result.Add(nextLowest);
                continue;
            }

            if (queried.Contains(nextLowest))
            {
                // Queried but not responded
                continue;
            }

            token.ThrowIfCancellationRequested();

            // TODO: In parallel?
            // So the paper mentioned that node that it need to query findnode for node that was not queried.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromMilliseconds(100));
            try
            {
                // Yea.. it does not mention what to do about the result.
                _ = await findNeighbourOp(nextLowest, cts.Token);
                result.AddRange(nextLowest);
            }
            catch (OperationCanceledException)
            {
                // Do nothing
            }
        }

        return result.ToArray();
    }

    public async Task Run(CancellationToken token)
    {
        await LookupNodesClosest(_currentNodeId, token);

        while (true)
        {
            await Bootstrap(token);

            await Task.Delay(_refreshInterval, token);
        }
    }

    public async Task Bootstrap(CancellationToken token)
    {
        await LookupNodesClosest(_currentNodeId, token);

        token.ThrowIfCancellationRequested();

        // Refreshes all bucket. one by one. That is not empty.
        // A refresh means to do a k-nearest node lookup for a random hash for that particular bucket.
        for (var i = 0; i < _buckets.Length; i++)
        {
            token.ThrowIfCancellationRequested();

            if (_buckets[i].Count > 0)
            {
                THash nodeToLookup = _distanceCalculator.RandomizeHashAtDistance(_currentNodeId, i);
                await LookupNodesClosest(nodeToLookup, token);
            }
        }
    }

    public IEnumerable<THash> IterateNeighbour(THash hash)
    {
        int startingDistance = _distanceCalculator.CalculateDistance(_currentNodeId, hash);
        foreach (var bucketToGet in EnumerateBucket(startingDistance))
        {
            foreach (THash bucketContent in _buckets[bucketToGet].GetAll())
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
        while (left > 0 || right <= _distanceCalculator.MaxDistance)
        {
            if (left > 0)
            {
                yield return left;
            }

            if (right <= _distanceCalculator.MaxDistance)
            {
                yield return right;
            }

            left -= 1;
            right += 1;
        }
    }

    private KBucket<THash, TValue> GetBucket(THash hash)
    {
        int idx = _distanceCalculator.CalculateDistance(hash, _currentNodeId);
        return _buckets[idx];
    }

    private void OnIncomingMessageFrom(THash sender)
    {
        SeedNode(sender);
        _peerFailures.Delete(sender);
    }

    private void OnRequestFailed(THash receiver)
    {
        if (!_peerFailures.TryGet(receiver, out var currentFailure))
        {
            _peerFailures.Set(receiver, 1);
            return;
        }

        if (currentFailure >= 5)
        {
            GetBucket(receiver).Remove(receiver);
            _peerFailures.Delete(receiver);

        }

        _peerFailures.Set(receiver, currentFailure + 1);
    }

    public Task Ping(THash sender, CancellationToken token)
    {
        OnIncomingMessageFrom(sender);
        return Task.CompletedTask;
    }

    public Task<THash[]> FindNeighbours(THash sender, THash hash, CancellationToken token)
    {
        OnIncomingMessageFrom(sender);
        return Task.FromResult(IterateNeighbour(hash).Take(_kSize).ToArray());
    }

    public Task<FindValueResponse<THash, TValue>> FindValue(THash sender, THash hash, CancellationToken token)
    {
        OnIncomingMessageFrom(sender);

        if (_store.TryGetValue(hash, out TValue value))
        {
            return Task.FromResult(new FindValueResponse<THash, TValue>(true, value, Array.Empty<THash>()));
        }

        return Task.FromResult(new FindValueResponse<THash, TValue>(false, default, IterateNeighbour(hash).Take(_kSize).ToArray()));
    }

    public bool Debug = false;

    private void PrintDebug(string debugInfo)
    {
        if (!Debug) return;
        Console.Error.WriteLine(debugInfo);
    }

    /// <summary>
    /// Monitor requests for success or failure.
    /// </summary>
    /// <param name="implementation"></param>
    /// <param name="kademlia"></param>
    private class MessageSenderMonitor(IMessageSender<THash, TValue> implementation, Kademlia<THash, TValue> kademlia) : IMessageSender<THash, TValue>
    {
        public async Task Ping(THash receiver, CancellationToken token)
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

        public async Task<THash[]> FindNeighbours(THash receiver, THash hash, CancellationToken token)
        {
            try
            {
                THash[] res = await implementation.FindNeighbours(receiver, hash, token);
                kademlia.OnIncomingMessageFrom(receiver);
                return res;
            }
            catch (OperationCanceledException)
            {
                kademlia.OnRequestFailed(receiver);
                throw;
            }
        }

        public Task<FindValueResponse<THash, TValue>> FindValue(THash receiver, THash hash, CancellationToken token)
        {
            try
            {
                Task<FindValueResponse<THash, TValue>> res = implementation.FindValue(receiver, hash, token);
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
