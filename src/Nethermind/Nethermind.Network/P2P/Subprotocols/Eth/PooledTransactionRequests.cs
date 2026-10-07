// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Eth;

/// <summary>Owns bounded request snapshots until claimed, evicted, or disposed.</summary>
internal sealed class PooledTransactionRequests : IDisposable
{
    private readonly Lock _lock = new();
    private readonly ArrayPool<ValueHash256> _pool;
    private readonly Dictionary<long, int> _indices;
    private readonly Entry[] _entries;
    private int _allocated;
    private int _free = -1;
    private int _first = -1;
    private int _last = -1;
    private bool _disposed;

    internal PooledTransactionRequests(int capacity, ArrayPool<ValueHash256>? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _pool = pool ?? ArrayPool<ValueHash256>.Shared;
        _indices = new(capacity);
        _entries = new Entry[capacity];
    }

    internal void Add(long id, ReadOnlySpan<ValueHash256> hashes)
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (_indices.TryGetValue(id, out int existing)) Remove(existing).Dispose();
            else if (_indices.Count == _entries.Length) Remove(_first).Dispose();

            ValueHash256[] array = _pool.Rent(hashes.Length);
            hashes.CopyTo(array);
            int index;
            if (_free >= 0)
            {
                index = _free;
                _free = _entries[index].Next;
            }
            else index = _allocated++;

            _entries[index] = new Entry(id, new Request(_pool, array, hashes.Length), _last);
            if (_last >= 0) _entries[_last].Next = index;
            else _first = index;
            _last = index;
            _indices.Add(id, index);
        }
    }

    internal bool TryClaim(long id, out Request request)
    {
        lock (_lock)
        {
            if (_indices.TryGetValue(id, out int index))
            {
                request = Remove(index);
                return true;
            }
            request = default;
            return false;
        }
    }

    private Request Remove(int index)
    {
        ref Entry entry = ref _entries[index];
        _indices.Remove(entry.Id);
        if (entry.Previous >= 0) _entries[entry.Previous].Next = entry.Next;
        else _first = entry.Next;
        if (entry.Next >= 0) _entries[entry.Next].Previous = entry.Previous;
        else _last = entry.Previous;
        Request request = entry.Request;
        entry = default;
        entry.Next = _free;
        _free = index;
        return request;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            while (_first >= 0) Remove(_first).Dispose();
        }
    }

    private struct Entry(long id, Request request, int previous)
    {
        internal long Id = id;
        internal Request Request = request;
        internal int Previous = previous;
        internal int Next = -1;
    }

    /// <summary>A claimed snapshot, owned exclusively by the response handler.</summary>
    internal readonly struct Request(ArrayPool<ValueHash256> pool, ValueHash256[] hashes, int count) : IDisposable
    {
        internal ReadOnlySpan<ValueHash256> Hashes => hashes.AsSpan(0, count);
        public void Dispose() => pool.Return(hashes);
    }
}
