// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NonBlocking;

namespace Nethermind.Kademlia;

public class DoubleEndedLru<TNode, TKadKey>(int capacity)
    where TNode : notnull
    where TKadKey : notnull
{
    private readonly object _lock = new();

    private readonly LinkedList<(TKadKey, TNode)> _queue = new();
    private readonly ConcurrentDictionary<TKadKey, LinkedListNode<(TKadKey, TNode)>> _hashMapping = new();
    public int Count => _queue.Count;

    public BucketAddResult AddOrRefresh(in TKadKey hash, TNode node)
    {
        lock (_lock)
        {
            if (_hashMapping.TryGetValue(hash, out LinkedListNode<(TKadKey, TNode)>? listNode))
            {
                _queue.Remove(listNode);
                listNode.Value = (hash, node);
                _queue.AddFirst(listNode);
                return BucketAddResult.Refreshed;
            }

            if (_queue.Count >= capacity)
            {
                return BucketAddResult.Full;
            }

            listNode = _queue.AddFirst((hash, node));
            _hashMapping.TryAdd(hash, listNode);
            return BucketAddResult.Added;
        }
    }

    public bool TryPopHead(out TKadKey hash, out TNode? node)
    {
        lock (_lock)
        {
            LinkedListNode<(TKadKey, TNode)>? front = _queue.First;
            if (front == null)
            {
                hash = default!;
                node = default;
                return false;
            }

            _queue.Remove(front);
            hash = front.Value.Item1;
            node = front.Value.Item2;
            _hashMapping.TryRemove(front.Value.Item1, out front);

            return true;
        }
    }

    public bool TryGetLast(out TNode? last)
    {
        lock (_lock)
        {
            LinkedListNode<(TKadKey, TNode)>? lastNode = _queue.Last;
            if (lastNode == null)
            {
                last = default;
                return false;
            }

            last = lastNode.Value.Item2;
            return true;
        }
    }

    public bool Remove(TKadKey hash)
    {
        lock (_lock)
        {
            if (_hashMapping.TryRemove(hash, out LinkedListNode<(TKadKey, TNode)>? listNode))
            {
                _queue.Remove(listNode);
                return true;
            }

            return false;
        }
    }

    public TNode[] GetAll()
    {
        lock (_lock)
        {
            TNode[] result = new TNode[_queue.Count];
            int i = 0;
            foreach ((TKadKey, TNode node) entry in _queue) result[i++] = entry.node;
            return result;
        }
    }

    public (TKadKey, TNode)[] GetAllWithHash()
    {
        lock (_lock)
        {
            (TKadKey, TNode)[] result = new (TKadKey, TNode)[_queue.Count];
            int i = 0;
            foreach ((TKadKey, TNode) entry in _queue) result[i++] = entry;
            return result;
        }
    }

    public bool Contains(in TKadKey hash) => _hashMapping.ContainsKey(hash);

    public TNode? GetByHash(TKadKey hash)
    {
        if (_hashMapping.TryGetValue(hash, out LinkedListNode<(TKadKey, TNode)>? listNode))
        {
            return listNode.Value.Item2;
        }

        return default;
    }
}
