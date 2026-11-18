// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.LightClient;

internal sealed class VerifiedMemoryCache : IDisposable
{
    internal const long Capacity = 32 * 1024 * 1024;
    private readonly Lock _gate = new();
    private readonly Dictionary<object, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _recent = new();
    private long _weight;
    private bool _disposed;

    internal bool TryGetValue<T>(object key, out T? value)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(key, out LinkedListNode<Entry>? entry))
            {
                _recent.Remove(entry);
                _recent.AddLast(entry);
                value = (T)entry.Value.Value;
                return true;
            }
            value = default;
            return false;
        }
    }

    internal void Set<T>(object key, T value, long weight)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.Remove(key, out LinkedListNode<Entry>? previous))
            {
                _recent.Remove(previous);
                _weight -= previous.Value.Weight;
            }
            if (weight > Capacity) return;
            while (_weight > Capacity - weight)
            {
                LinkedListNode<Entry> oldest = _recent.First!;
                _recent.RemoveFirst();
                _entries.Remove(oldest.Value.Key);
                _weight -= oldest.Value.Weight;
            }
            _entries.Add(key, _recent.AddLast(new Entry(key, value!, weight)));
            _weight += weight;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _entries.Clear();
            _recent.Clear();
            _weight = 0;
        }
    }

    private readonly record struct Entry(object Key, object Value, long Weight);
}
