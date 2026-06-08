// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NonBlocking;

namespace Nethermind.Kademlia;

public class DoubleEndedLru<TKey, TValue>(int capacity)
    where TKey : notnull
    where TValue : notnull
{
    private readonly object _lock = new();

    private readonly LinkedList<(TKey Key, TValue Value)> _queue = new();
    private readonly ConcurrentDictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _index = new();
    public int Count => _queue.Count;

    public BucketAddResult AddOrRefresh(in TKey key, TValue value)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(key, out LinkedListNode<(TKey Key, TValue Value)>? listNode))
            {
                _queue.Remove(listNode);
                listNode.Value = (key, value);
                _queue.AddFirst(listNode);
                return BucketAddResult.Refreshed;
            }

            if (_queue.Count >= capacity)
            {
                return BucketAddResult.Full;
            }

            listNode = _queue.AddFirst((key, value));
            _index.TryAdd(key, listNode);
            return BucketAddResult.Added;
        }
    }

    public bool TryPopHead(out TKey key, out TValue? value)
    {
        lock (_lock)
        {
            LinkedListNode<(TKey Key, TValue Value)>? front = _queue.First;
            if (front == null)
            {
                key = default!;
                value = default;
                return false;
            }

            _queue.Remove(front);
            key = front.Value.Key;
            value = front.Value.Value;
            _index.TryRemove(front.Value.Key, out front);

            return true;
        }
    }

    public bool TryGetLast(out TValue? last)
    {
        lock (_lock)
        {
            LinkedListNode<(TKey Key, TValue Value)>? lastNode = _queue.Last;
            if (lastNode == null)
            {
                last = default;
                return false;
            }

            last = lastNode.Value.Value;
            return true;
        }
    }

    public bool Remove(TKey key)
    {
        lock (_lock)
        {
            if (_index.TryRemove(key, out LinkedListNode<(TKey Key, TValue Value)>? listNode))
            {
                _queue.Remove(listNode);
                return true;
            }

            return false;
        }
    }

    public TValue[] GetAll()
    {
        lock (_lock)
        {
            TValue[] result = new TValue[_queue.Count];
            int i = 0;
            foreach ((TKey Key, TValue Value) entry in _queue) result[i++] = entry.Value;
            return result;
        }
    }

    public (TKey Key, TValue Value)[] GetAllWithKey()
    {
        lock (_lock)
        {
            (TKey Key, TValue Value)[] result = new (TKey Key, TValue Value)[_queue.Count];
            int i = 0;
            foreach ((TKey Key, TValue Value) entry in _queue) result[i++] = entry;
            return result;
        }
    }

    public bool Contains(in TKey key) => _index.ContainsKey(key);

    public TValue? GetByKey(TKey key)
    {
        if (_index.TryGetValue(key, out LinkedListNode<(TKey Key, TValue Value)>? listNode))
        {
            return listNode.Value.Value;
        }

        return default;
    }
}
