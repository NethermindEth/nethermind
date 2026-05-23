// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Threading;
using NonBlocking;

namespace Nethermind.Kademlia;

public class DoubleEndedLru<TNode>(int capacity) where TNode : notnull
{
    private readonly McsLock _lock = new();

    private readonly LinkedList<(KademliaHash, TNode)> _queue = new();
    private readonly ConcurrentDictionary<KademliaHash, LinkedListNode<(KademliaHash, TNode)>> _hashMapping = new();
    public int Count => _queue.Count;

    public BucketAddResult AddOrRefresh(in KademliaHash hash, TNode node)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        if (_hashMapping.TryGetValue(hash, out LinkedListNode<(KademliaHash, TNode)>? listNode))
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

    public bool TryPopHead(out KademliaHash hash, out TNode? node)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        LinkedListNode<(KademliaHash, TNode)>? front = _queue.First;
        if (front == null)
        {
            hash = default;
            node = default;
            return false;
        }

        _queue.Remove(front);
        hash = front.Value.Item1;
        node = front.Value.Item2;
        _hashMapping.TryRemove(front.Value.Item1, out front);

        return true;
    }

    public bool TryGetLast(out TNode? last)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        LinkedListNode<(KademliaHash, TNode)>? lastNode = _queue.Last;
        if (lastNode == null)
        {
            last = default;
            return false;
        }

        last = lastNode.Value.Item2;
        return true;
    }

    public bool Remove(KademliaHash hash)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        if (_hashMapping.TryRemove(hash, out LinkedListNode<(KademliaHash, TNode)>? listNode))
        {
            _queue.Remove(listNode);
            return true;
        }

        return false;
    }

    public TNode[] GetAll()
    {
        using McsLock.Disposable _ = _lock.Acquire();
        TNode[] result = new TNode[_queue.Count];
        int i = 0;
        foreach ((KademliaHash, TNode node) entry in _queue) result[i++] = entry.node;
        return result;
    }

    public (KademliaHash, TNode)[] GetAllWithHash()
    {
        using McsLock.Disposable _ = _lock.Acquire();
        (KademliaHash, TNode)[] result = new (KademliaHash, TNode)[_queue.Count];
        int i = 0;
        foreach ((KademliaHash, TNode) entry in _queue) result[i++] = entry;
        return result;
    }

    public bool Contains(in KademliaHash hash) => _hashMapping.ContainsKey(hash);

    public TNode? GetByHash(KademliaHash hash)
    {
        if (_hashMapping.TryGetValue(hash, out LinkedListNode<(KademliaHash, TNode)>? listNode))
        {
            return listNode.Value.Item2;
        }

        return default;
    }
}
