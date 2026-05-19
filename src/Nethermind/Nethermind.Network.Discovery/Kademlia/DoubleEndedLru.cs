// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using NonBlocking;

namespace Nethermind.Network.Discovery.Kademlia;

public class DoubleEndedLru<TNode>(int capacity) where TNode : notnull
{
    private readonly McsLock _lock = new();

    private readonly LinkedList<(ValueHash256, TNode)> _queue = new();
    private readonly ConcurrentDictionary<ValueHash256, LinkedListNode<(ValueHash256, TNode)>> _hashMapping = new();
    public int Count => _queue.Count;

    public BucketAddResult AddOrRefresh(in ValueHash256 hash, TNode node)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        if (_hashMapping.TryGetValue(hash, out LinkedListNode<(ValueHash256, TNode)>? listNode))
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

    public bool TryPopHead(out ValueHash256 hash, out TNode? node)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        LinkedListNode<(ValueHash256, TNode)>? front = _queue.First;
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

        LinkedListNode<(ValueHash256, TNode)>? lastNode = _queue.Last;
        if (lastNode == null)
        {
            last = default;
            return false;
        }

        last = lastNode.Value.Item2;
        return true;
    }

    public bool Remove(ValueHash256 hash)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        if (_hashMapping.TryRemove(hash, out LinkedListNode<(ValueHash256, TNode)>? listNode))
        {
            _queue.Remove(listNode);
            return true;
        }

        return false;
    }

    public TNode[] GetAll() => [.. _hashMapping.Select(kv => kv.Value.Value.Item2)];

    public IEnumerable<(ValueHash256, TNode)> GetAllWithHash() => _queue;

    public bool Contains(in ValueHash256 hash) => _hashMapping.ContainsKey(hash);

    public TNode? GetByHash(ValueHash256 hash)
    {
        if (_hashMapping.TryGetValue(hash, out LinkedListNode<(ValueHash256, TNode)>? listNode))
        {
            return listNode.Value.Item2;
        }

        return default;
    }
}
