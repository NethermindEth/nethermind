// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Kademlia;

internal static class RecentNodeFilter
{
    private const int MaxBucketSizeForLimit = 16;

    public static int GetLimit(int bucketSize, int maxDistance, int minimumCount)
        => Math.Max(minimumCount, Math.Min(bucketSize, MaxBucketSizeForLimit) * maxDistance);
}

internal sealed class RecentNodeFilter<TKey>(int maxCount)
    where TKey : notnull
{
    private readonly Dictionary<TKey, long> _nodes = new(maxCount);
    private readonly Lock _lock = new();
    private Queue<(TKey NodeId, long Generation)> _recentNodes = new(maxCount);
    private long _generation;

    public bool TryReserve(TKey nodeId)
    {
        lock (_lock)
        {
            if (_nodes.ContainsKey(nodeId))
            {
                return false;
            }

            long generation = unchecked(++_generation);
            _nodes.Add(nodeId, generation);
            _recentNodes.Enqueue((nodeId, generation));
            Trim();

            return true;
        }
    }

    public void Release(TKey nodeId)
    {
        lock (_lock)
        {
            _nodes.Remove(nodeId);
            DropReleasedHeadEntries();
            if (_recentNodes.Count > Math.Max(maxCount * 2, 256))
            {
                CompactQueue();
            }
        }
    }

    private void Trim()
    {
        DropReleasedHeadEntries();
        while (_nodes.Count > maxCount && _recentNodes.TryDequeue(out (TKey NodeId, long Generation) oldestNode))
        {
            if (_nodes.TryGetValue(oldestNode.NodeId, out long generation) && generation == oldestNode.Generation)
            {
                _nodes.Remove(oldestNode.NodeId);
            }
        }
    }

    private void DropReleasedHeadEntries()
    {
        while (_recentNodes.TryPeek(out (TKey NodeId, long Generation) oldestNode) &&
               (!_nodes.TryGetValue(oldestNode.NodeId, out long generation) || generation != oldestNode.Generation))
        {
            _recentNodes.Dequeue();
        }
    }

    private void CompactQueue()
    {
        Queue<(TKey NodeId, long Generation)> compacted = new(_nodes.Count);
        foreach ((TKey NodeId, long Generation) node in _recentNodes)
        {
            if (_nodes.TryGetValue(node.NodeId, out long generation) && generation == node.Generation)
            {
                compacted.Enqueue(node);
            }
        }

        _recentNodes = compacted;
    }
}
