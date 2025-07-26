// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;

namespace Nethermind.Network.Discovery.Kademlia;

public class DoubleEndedLru<TNode, THash>(int capacity) where TNode : notnull where THash : notnull
{
    private McsLock _lock = new McsLock();

    private LinkedList<(THash, TNode)> _queue = new();
    private ConcurrentDictionary<THash, LinkedListNode<(THash, TNode)>> _hashMapping = new();
    public int Count => _queue.Count;

    public BucketAddResult AddOrRefresh(in THash hash, TNode node)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        if (_hashMapping.TryGetValue(hash, out var listNode))
        {
            _queue.Remove(listNode);
            _queue.AddFirst(listNode);
            return BucketAddResult.Refreshed;
        }

        if (_queue.Count >= capacity)
        {
            return BucketAddResult.Full;
        }

        listNode = _queue.AddFirst((hash, node));
        if (_hashMapping.TryAdd(hash, listNode) && _queue.Count <= capacity) return BucketAddResult.Added;

        _queue.Remove((hash, node));
        _hashMapping.TryRemove(hash, out listNode);

        return BucketAddResult.Full;
    }

    public bool TryPopHead(out TNode? node)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        LinkedListNode<(THash, TNode)>? front = _queue.First;
        if (front == null)
        {
            node = default;
            return false;
        }

        _queue.Remove(front);
        node = front.Value.Item2;
        _hashMapping.TryRemove(front.Value.Item1, out front);

        return true;
    }

    public bool TryGetLast(out TNode? last)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        LinkedListNode<(THash, TNode)>? lastNode = _queue.Last;
        if (lastNode == null)
        {
            last = default;
            return false;
        }

        last = lastNode.Value.Item2;
        return true;
    }

    public bool Remove(THash hash)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        if (_hashMapping.TryRemove(hash, out var listNode))
        {
            _queue.Remove(listNode);
            return true;
        }

        return false;
    }

    public TNode[] GetAll()
    {
        return _hashMapping.Select(kv => kv.Value.Value.Item2).ToArray();
    }

    public IEnumerable<(THash, TNode)> GetAllWithHash()
    {
        return _queue;
    }

    public bool Contains(in THash hash)
    {
        return _hashMapping.ContainsKey(hash);
    }

    public TNode? GetByHash(THash hash)
    {
        if (_hashMapping.TryGetValue(hash, out LinkedListNode<(THash, TNode)>? listNode))
        {
            return listNode.Value.Item2;
        }

        return default;
    }
}
