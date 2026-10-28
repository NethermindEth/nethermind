// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;

namespace Nethermind.Blockchain;

/// <summary>Bounds private encoded snapshots by entry count and byte size.</summary>
/// <remarks>
/// Readers must not mutate returned arrays. Mutations and their database writes must hold <see cref="SyncRoot"/>;
/// generation-checked publication prevents an in-flight read from refilling an invalidated snapshot.
/// Never invoke a pending overlay while holding this lock: its write callbacks may acquire it.
/// </remarks>
internal sealed class EncodedRlpCache<TKey>(int capacity, int byteCapacity) where TKey : notnull
{
    private readonly int _capacity = capacity;
    private readonly int _byteCapacity = byteCapacity;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _lru = new();
    private int _bytes;
    private long _generation;

    private readonly record struct Entry(TKey Key, byte[] Rlp);

    internal Lock SyncRoot { get; } = new();
    internal int ByteCapacity => _byteCapacity;

    internal bool TryGet(TKey key, out byte[]? rlp, out long generation)
    {
        lock (SyncRoot)
        {
            generation = _generation;
            if (_entries.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                _lru.Remove(node);
                _lru.AddLast(node);
                rlp = node.Value.Rlp;
                return true;
            }

            rlp = null;
            return false;
        }
    }

    internal void Publish(TKey key, byte[] rlp, long generation)
    {
        lock (SyncRoot)
        {
            if (generation != _generation || _capacity <= 0 || rlp.Length > _byteCapacity || _entries.ContainsKey(key)) return;

            while (_entries.Count >= _capacity || _bytes > _byteCapacity - rlp.Length)
            {
                LinkedListNode<Entry> oldest = _lru.First!;
                _entries.Remove(oldest.Value.Key);
                _lru.RemoveFirst();
                _bytes -= oldest.Value.Rlp.Length;
            }

            LinkedListNode<Entry> node = new(new Entry(key, rlp));
            _entries.Add(key, node);
            _lru.AddLast(node);
            _bytes += rlp.Length;
        }
    }

    internal void Invalidate(TKey key)
    {
        _generation++;
        if (_entries.Remove(key, out LinkedListNode<Entry>? node))
        {
            _lru.Remove(node);
            _bytes -= node.Value.Rlp.Length;
        }
    }

    internal void Clear()
    {
        _generation++;
        _entries.Clear();
        _lru.Clear();
        _bytes = 0;
    }
}
