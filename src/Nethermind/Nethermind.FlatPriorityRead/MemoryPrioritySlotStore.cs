// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat;

namespace Nethermind.FlatPriorityRead;

/// <summary>An <see cref="IPrioritySlotStore"/> in process memory: a base table per contract plus one immutable delta per commit.</summary>
/// <remarks>
/// <para>
/// A reader leases the view of the last commit, which lists the deltas not yet folded into the base, newest first. A
/// lookup takes the first delta that has the slot and falls through to the base otherwise.
/// </para>
/// <para>
/// A delta is folded into the base in place once no live view is older than it. Every live view then lists that
/// delta, so the base only changes for slots each live reader finds in its own deltas first. A commit made while no
/// reader holds any view writes straight into the base.
/// </para>
/// </remarks>
public sealed class MemoryPrioritySlotStore : IPrioritySlotStore
{
    private readonly Lock _lock = new();
    private readonly PrioritySlotTable?[] _tables = new PrioritySlotTable?[PrioritySlotEncoding.MaxContracts];
    private readonly List<Delta> _unfolded = [];

    // Live views oldest first, ending with _current; ones released out of order wait behind an older live one.
    private readonly Queue<View> _views = new();
    private View _current;
    private long _version;

    public MemoryPrioritySlotStore()
    {
        _current = new View(_version, default, []);
        _views.Enqueue(_current);
    }

    public IPrioritySlotStore.IReader BeginRead()
    {
        // Under the lock so a commit sees every lease when it decides what it may change in place.
        using Lock.Scope _ = _lock.EnterScope();
        _current.Lease();
        return new Reader(this, _current);
    }

    public IPrioritySlotStore.IWriter BeginWrite() => new Writer(this);

    public void Dispose() { }

    private void Commit(Dictionary<(byte Contract, ValueHash256 Key), UInt256?> changes, in StateId state)
    {
        using Lock.Scope _ = _lock.EnterScope();
        while (_views.Peek().IsReleased) _views.Dequeue();

        long horizon = _views.Peek().Version;
        int folded = 0;
        while (folded < _unfolded.Count && _unfolded[folded].Version <= horizon) Apply(_unfolded[folded++].Changes);
        _unfolded.RemoveRange(0, folded);

        long version = ++_version;
        if (_views.Count == 1 && _current.HasNoReaders) Apply(changes);
        else _unfolded.Add(new Delta(version, changes));

        Delta[] deltas = new Delta[_unfolded.Count];
        for (int i = 0; i < deltas.Length; i++) deltas[i] = _unfolded[^(i + 1)];

        View previous = _current;
        _current = new View(version, state, deltas);
        _views.Enqueue(_current);
        previous.Release();
    }

    private void Apply(Dictionary<(byte Contract, ValueHash256 Key), UInt256?> changes)
    {
        foreach (KeyValuePair<(byte Contract, ValueHash256 Key), UInt256?> change in changes)
        {
            (byte contract, ValueHash256 key) = change.Key;
            PrioritySlotTable? table = _tables[contract];
            if (table is null) Volatile.Write(ref _tables[contract], table = new PrioritySlotTable());

            if (change.Value is { } value) table.Set(key, value);
            else table.Remove(key);
        }
    }

    private sealed record Delta(long Version, Dictionary<(byte Contract, ValueHash256 Key), UInt256?> Changes);

    private sealed class View(long version, StateId state, Delta[] deltas)
    {
        // The store holds one lease while the view is current.
        private int _leases = 1;

        public long Version => version;
        public StateId State => state;
        public Delta[] Deltas => deltas;
        public bool IsReleased => Volatile.Read(ref _leases) == 0;
        public bool HasNoReaders => Volatile.Read(ref _leases) == 1;

        public void Lease() => Interlocked.Increment(ref _leases);
        public void Release() => Interlocked.Decrement(ref _leases);
    }

    private sealed class Reader(MemoryPrioritySlotStore store, View view) : IPrioritySlotStore.IReader
    {
        private int _disposed;

        public StateId State => view.State;

        public bool TryGet(byte contract, in ValueHash256 slotKey, out UInt256 value)
        {
            foreach (Delta delta in view.Deltas)
            {
                if (delta.Changes.TryGetValue((contract, slotKey), out UInt256? change))
                {
                    value = change.GetValueOrDefault();
                    return change.HasValue;
                }
            }

            PrioritySlotTable? table = Volatile.Read(ref store._tables[contract]);
            if (table is not null) return table.TryGet(slotKey, out value);

            value = default;
            return false;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) view.Release();
        }
    }

    private sealed class Writer(MemoryPrioritySlotStore store) : IPrioritySlotStore.IWriter
    {
        private Dictionary<(byte Contract, ValueHash256 Key), UInt256?> _changes = [];

        public void Set(byte contract, in ValueHash256 slotKey, in UInt256 value) => _changes[(contract, slotKey)] = value;

        public void Remove(byte contract, in ValueHash256 slotKey) => _changes[(contract, slotKey)] = null;

        public void Commit(in StateId state)
        {
            store.Commit(_changes, state);
            _changes = [];
        }

        public void Dispose() { }
    }
}
