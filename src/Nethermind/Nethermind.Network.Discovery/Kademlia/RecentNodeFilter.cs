// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Kademlia;

internal sealed class RecentNodeFilter<TKey>(int maxCount)
    where TKey : notnull
{
    private readonly LinkedList<TKey> _recentNodes = [];
    private readonly Dictionary<TKey, LinkedListNode<TKey>> _nodes = new(maxCount);
    private readonly Lock _lock = new();

    public bool TryReserve(TKey nodeId)
    {
        lock (_lock)
        {
            if (_nodes.ContainsKey(nodeId))
            {
                return false;
            }

            LinkedListNode<TKey> listNode = _recentNodes.AddLast(nodeId);
            _nodes.Add(nodeId, listNode);
            while (_nodes.Count > maxCount)
            {
                LinkedListNode<TKey> oldestNode = _recentNodes.First!;
                _recentNodes.RemoveFirst();
                _nodes.Remove(oldestNode.Value);
            }

            return true;
        }
    }

    public void Release(TKey nodeId)
    {
        lock (_lock)
        {
            if (_nodes.Remove(nodeId, out LinkedListNode<TKey>? listNode))
            {
                _recentNodes.Remove(listNode);
            }
        }
    }
}
