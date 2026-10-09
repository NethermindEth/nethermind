// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Caching;

internal sealed class LruSlots<T>(int maxCapacity, int startCapacity)
{
    private Entry[] _entries = startCapacity == 0 ? [] : new Entry[startCapacity];
    private int _used;
    private int _free = -1;
    public int LeastRecentlyUsed { get; private set; } = -1;
    public int Capacity => _entries.Length;
    public static int EntrySize => Unsafe.SizeOf<Entry>();

    public ref T Value(int index) => ref _entries[index].Value;

    public int AddMostRecent(T value)
    {
        int index;
        if (_free >= 0)
        {
            index = _free;
            _free = _entries[index].Next;
        }
        else
        {
            if (_used == _entries.Length)
            {
                int capacity = (int)Math.Min(maxCapacity, Math.Max(4L, (long)_entries.Length * 2));
                Array.Resize(ref _entries, capacity);
            }
            index = _used++;
        }

        _entries[index].Value = value;
        LinkMostRecent(index);
        return index;
    }

    public void MoveToMostRecent(int index)
    {
        if (_entries[LeastRecentlyUsed].Previous == index) return;
        Unlink(index);
        LinkMostRecent(index);
    }

    public void Remove(int index)
    {
        Unlink(index);
        _entries[index] = new Entry { Next = _free };
        _free = index;
    }

    /// <summary>Releases stored references while retaining slot capacity for allocation-free refill.</summary>
    public void Clear()
    {
        _entries.AsSpan(0, _used).Clear();
        _used = 0;
        _free = -1;
        LeastRecentlyUsed = -1;
    }

    private void Unlink(int index)
    {
        int next = _entries[index].Next;
        int previous = _entries[index].Previous;
        if (next == index)
        {
            LeastRecentlyUsed = -1;
        }
        else
        {
            _entries[next].Previous = previous;
            _entries[previous].Next = next;
            if (LeastRecentlyUsed == index) LeastRecentlyUsed = next;
        }
    }

    private void LinkMostRecent(int index)
    {
        if (LeastRecentlyUsed < 0)
        {
            _entries[index].Next = index;
            _entries[index].Previous = index;
            LeastRecentlyUsed = index;
        }
        else
        {
            int previous = _entries[LeastRecentlyUsed].Previous;
            _entries[index].Next = LeastRecentlyUsed;
            _entries[index].Previous = previous;
            _entries[previous].Next = index;
            _entries[LeastRecentlyUsed].Previous = index;
        }
    }

    private struct Entry
    {
        public T Value;
        public int Previous;
        public int Next;
    }
}
