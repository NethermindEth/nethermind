// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Threading;
using NonBlocking;

namespace Nethermind.Network.Discovery.Kademlia;

// TODO: Combine with LruCace?
public class DoubleEndedLru<THash>(int capacity) where THash : notnull
{
    // Double check if can be done locklesly
    private McsLock _lock = new McsLock();

    private LinkedList<THash> _queue = new();
    private ConcurrentDictionary<THash, LinkedListNode<THash>> _hashMapping = new();
    public int Count => _queue.Count;

    public bool AddOrRefresh(THash hash)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        if (_hashMapping.TryGetValue(hash, out var node))
        {
            _queue.Remove(node);
            _queue.AddFirst(node);
            return true;
        }

        if (_queue.Count >= capacity)
        {
            return false;
        }

        node = _queue.AddFirst(hash);
        if (_hashMapping.TryAdd(hash, node) && _queue.Count <= capacity) return true;

        _queue.Remove(node);
        _hashMapping.TryRemove(hash, out node);

        return false;
    }

    public bool TryPopHead(out THash? hash)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        LinkedListNode<THash>? front = _queue.First;
        if (front == null)
        {
            hash = default;
            return false;
        }

        _queue.Remove(front);
        hash = front.Value;
        _hashMapping.TryRemove(front.Value, out front);

        Console.Error.WriteLine($"pop head {hash}");
        return true;
    }

    public bool TryGetLast(out THash? last)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        LinkedListNode<THash>? lastNode = _queue.Last;
        if (lastNode == null)
        {
            last = default;
            return false;
        }

        last = lastNode.Value;
        return true;
    }

    public void Remove(THash hash)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        Console.Error.WriteLine($"Removed {hash}");
        if (_hashMapping.TryRemove(hash, out var node))
        {
            _queue.Remove(node);
        }
    }

    public THash[] GetAll()
    {
        return _hashMapping.Select(kv => kv.Key).ToArray();
    }
}
