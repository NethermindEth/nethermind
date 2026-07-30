// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Kademlia;

/// <summary>
/// Fixed-capacity LRU map with O(1) access to both the most and the least recently used entry.
/// </summary>
/// <remarks>
/// Entries live in a preallocated array threaded into an intrusive doubly-linked list (head = most recently
/// used, tail = least recently used); removed slots are recycled through a free list, so steady-state
/// operations do not allocate. All members are thread-safe.
/// </remarks>
public class DoubleEndedLru<TKey, TValue>(int capacity)
    where TKey : notnull
    where TValue : notnull
{
    private const int None = -1;

    private struct Entry
    {
        public TKey Key;
        public TValue Value;
        public int Prev;
        public int Next;
    }

    private readonly Lock _lock = new();
    private readonly Entry[] _entries = new Entry[capacity];
    private readonly Dictionary<TKey, int> _index = new(capacity);
    private int _head = None;
    private int _tail = None;
    private int _freeList = None;
    private int _used;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _index.Count;
            }
        }
    }

    public BucketAddResult AddOrRefresh(in TKey key, TValue value) => AddOrRefresh(in key, value, out _);

    /// <param name="previous">The value previously stored under <paramref name="key"/> when the entry is refreshed; default otherwise.</param>
    public BucketAddResult AddOrRefresh(in TKey key, TValue value, out TValue? previous)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(key, out int i))
            {
                ref Entry entry = ref _entries[i];
                previous = entry.Value;
                entry.Value = value;
                MoveToHead(i);
                return BucketAddResult.Refreshed;
            }

            previous = default;
            if (_index.Count >= _entries.Length)
            {
                return BucketAddResult.Full;
            }

            int slot = TakeSlot();
            ref Entry added = ref _entries[slot];
            added.Key = key;
            added.Value = value;
            LinkAtHead(slot);
            _index.Add(key, slot);
            return BucketAddResult.Added;
        }
    }

    public bool TryPopHead(out TKey key, out TValue? value)
    {
        lock (_lock)
        {
            if (_head == None)
            {
                key = default!;
                value = default;
                return false;
            }

            int head = _head;
            key = _entries[head].Key;
            value = _entries[head].Value;
            _index.Remove(key);
            Unlink(head);
            ReleaseSlot(head);
            return true;
        }
    }

    public bool TryGetLast(out TValue? last)
    {
        lock (_lock)
        {
            if (_tail == None)
            {
                last = default;
                return false;
            }

            last = _entries[_tail].Value;
            return true;
        }
    }

    public bool Remove(TKey key)
    {
        lock (_lock)
        {
            if (!_index.Remove(key, out int i))
            {
                return false;
            }

            Unlink(i);
            ReleaseSlot(i);
            return true;
        }
    }

    public TValue[] GetAll()
    {
        lock (_lock)
        {
            TValue[] result = new TValue[_index.Count];
            int n = 0;
            for (int i = _head; i != None; i = _entries[i].Next)
            {
                result[n++] = _entries[i].Value;
            }

            return result;
        }
    }

    public (TKey Key, TValue Value)[] GetAllWithKey()
    {
        lock (_lock)
        {
            (TKey Key, TValue Value)[] result = new (TKey Key, TValue Value)[_index.Count];
            int n = 0;
            for (int i = _head; i != None; i = _entries[i].Next)
            {
                result[n++] = (_entries[i].Key, _entries[i].Value);
            }

            return result;
        }
    }

    internal int CopyAllWithKey((TKey Key, TValue Value)[] destination)
    {
        lock (_lock)
        {
            int n = 0;
            for (int i = _head; i != None; i = _entries[i].Next)
            {
                destination[n++] = (_entries[i].Key, _entries[i].Value);
            }

            return n;
        }
    }

    public bool Contains(in TKey key)
    {
        lock (_lock)
        {
            return _index.ContainsKey(key);
        }
    }

    public TValue? GetByKey(TKey key)
    {
        lock (_lock)
        {
            return _index.TryGetValue(key, out int i) ? _entries[i].Value : default;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _index.Clear();
            Array.Clear(_entries);
            _head = None;
            _tail = None;
            _freeList = None;
            _used = 0;
        }
    }

    private int TakeSlot()
    {
        if (_freeList == None)
        {
            return _used++;
        }

        int slot = _freeList;
        _freeList = _entries[slot].Next;
        return slot;
    }

    private void ReleaseSlot(int i)
    {
        ref Entry entry = ref _entries[i];
        // Drop the key/value references so released slots do not keep them alive.
        entry.Key = default!;
        entry.Value = default!;
        entry.Next = _freeList;
        _freeList = i;
    }

    private void LinkAtHead(int i)
    {
        ref Entry entry = ref _entries[i];
        entry.Prev = None;
        entry.Next = _head;
        if (_head != None)
        {
            _entries[_head].Prev = i;
        }
        else
        {
            _tail = i;
        }

        _head = i;
    }

    private void MoveToHead(int i)
    {
        if (_head == i)
        {
            return;
        }

        Unlink(i);
        LinkAtHead(i);
    }

    private void Unlink(int i)
    {
        ref Entry entry = ref _entries[i];
        if (entry.Prev == None)
        {
            _head = entry.Next;
        }
        else
        {
            _entries[entry.Prev].Next = entry.Next;
        }

        if (entry.Next == None)
        {
            _tail = entry.Prev;
        }
        else
        {
            _entries[entry.Next].Prev = entry.Prev;
        }
    }
}
