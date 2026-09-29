// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.P2P;

/// <summary>
/// In-memory cache of data column sidecars this node has validated, serving
/// <c>DataColumnSidecarsByRange</c>/<c>ByRoot</c>.
/// </summary>
/// <remarks>
/// This is a bounded recent-sidecar cache, not the spec-required persisted store (a node MUST be
/// able to serve these requests for <see cref="Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests"/>
/// epochs). Each served map retains at most <c>capacity</c> sidecars and evicts the lowest slot first, so
/// a flood of old-slot sidecars can never push out a newer block's columns, and the slots still retained
/// completely always form one suffix, reported by <see cref="EarliestCompletelyServableSlot"/>. The
/// <c>capacity</c> most recently given sidecars are held as well, so a verified range-synced column
/// that the retained set refuses is still found when its block is imported.
/// Fulu and Gloas sidecars sit in separate maps keyed by (block root, column): a slot can carry
/// competing blocks, so by-range serving must resolve the canonical root first and look up by
/// (root, column). A Gloas sidecar whose block is not yet known can be
/// parked as pending: pending sidecars are never returned by the served lookups, and move to the
/// served map only through <see cref="AddGloas"/> once verified against their block's bid.
/// </remarks>
public sealed class DataColumnSidecarPool(int capacity = 1 << 14)
{
    private readonly Lock _servedLock = new();
    private readonly SlotOrderedSidecars<DataColumnSidecar> _byRootAndColumn = new(capacity);
    private readonly SlotOrderedSidecars<DataColumnSidecarGloas> _gloasByRootAndColumn = new(capacity);
    private ulong? _firstGivenSlot;
    // Pending sidecars are unverified and peer-supplied, so they get a smaller bound than the served maps.
    private readonly int _maxPendingGloas = Math.Min(capacity, MaxPendingGloasSidecars);
    private readonly Lock _pendingLock = new();
    private readonly Dictionary<(Hash256 BlockRoot, ulong Column), List<DataColumnSidecarGloas>> _pendingByKey = [];
    private int _pendingCount;
    private ulong _pendingPrunedAtSlot;
    private readonly Dictionary<Hash256, ColumnWatch> _watches = [];
    private int _watchCount;

    /// <summary>The most unverified Gloas sidecars <see cref="AddPendingGloas"/> holds at once.</summary>
    public const int MaxPendingGloasSidecars = 1 << 10;

    /// <summary>The most unverified Gloas sidecars <see cref="AddPendingGloas"/> holds for one (root, column).</summary>
    /// <remarks>Availability runs a KZG batch per candidate, so this bounds that work per column; above one, so an earlier forgery cannot block the genuine sidecar alone.</remarks>
    public const int MaxPendingGloasCandidatesPerKey = 4;

    /// <summary>The distinct slots the served maps index, bounded by twice <paramref name="capacity"/>; for tests and diagnostics.</summary>
    internal int SlotIndexCount
    {
        get
        {
            lock (_servedLock)
            {
                return _byRootAndColumn.SlotCount + _gloasByRootAndColumn.SlotCount;
            }
        }
    }

    /// <summary>
    /// The lowest slot from which every sidecar this pool was given is still retained; <see cref="ulong.MaxValue"/> before the first one.
    /// </summary>
    /// <remarks>
    /// A lower slot either lost or was refused a sidecar, or is below the slot of the first sidecar this process received,
    /// so its columns cannot be assumed complete. Never decreases. A block that carried no blobs has no sidecars, so its
    /// slot never counts against this.
    /// </remarks>
    internal ulong EarliestCompletelyServableSlot
    {
        get
        {
            lock (_servedLock)
            {
                return Math.Max(_firstGivenSlot ?? ulong.MaxValue, Math.Max(_byRootAndColumn.IncompleteBelow, _gloasByRootAndColumn.IncompleteBelow));
            }
        }
    }

    public void Add(Hash256 blockRoot, ulong slot, DataColumnSidecar sidecar)
    {
        Action? wake;
        lock (_servedLock)
        {
            MarkGiven(slot);
            _byRootAndColumn.Set((blockRoot, sidecar.Index), slot, sidecar);
            wake = TakeWakeOnArrival(blockRoot, sidecar.Index);
        }

        wake?.Invoke();
    }

    /// <summary>
    /// Registers <paramref name="wake"/> to run once, on the thread that adds the last of the <paramref name="columns"/> not yet held for
    /// <paramref name="blockRoot"/>, replacing an earlier watch on the root.
    /// </summary>
    /// <param name="gloas">Whether the columns are Gloas sidecars, where a parked candidate counts as held.</param>
    /// <returns>Whether the watch is set; <c>false</c> when every column is held already, so no arrival would wake it.</returns>
    /// <remarks>
    /// A column counts once it is given to the pool, whether or not it later verifies, so the woken retry decides availability.
    /// Only roots watched cost anything per sidecar, and the caller bounds the watches by the blocks it waits for.
    /// </remarks>
    internal bool TryWatch(Hash256 blockRoot, IReadOnlyList<ulong> columns, bool gloas, Action wake)
    {
        HashSet<ulong> missing = [];
        lock (_servedLock)
        {
            foreach (ulong column in columns)
            {
                if (!(gloas ? _gloasByRootAndColumn.TryGet((blockRoot, column), out _) || HasPendingGloas(blockRoot, column) : _byRootAndColumn.TryGet((blockRoot, column), out _)))
                {
                    missing.Add(column);
                }
            }

            if (missing.Count == 0)
            {
                _watches.Remove(blockRoot);
                Volatile.Write(ref _watchCount, _watches.Count);
                return false;
            }

            _watches[blockRoot] = new ColumnWatch(missing, wake);
            Volatile.Write(ref _watchCount, _watches.Count);
            return true;
        }
    }

    /// <summary>Removes the watch on <paramref name="blockRoot"/>, if any.</summary>
    internal void Unwatch(Hash256 blockRoot)
    {
        lock (_servedLock)
        {
            _watches.Remove(blockRoot);
            Volatile.Write(ref _watchCount, _watches.Count);
        }
    }

    /// <summary>The watched roots' current count; for tests and diagnostics.</summary>
    internal int WatchCount
    {
        get
        {
            lock (_servedLock)
            {
                return _watches.Count;
            }
        }
    }

    private Action? TakeWakeOnArrival(Hash256 blockRoot, ulong column)
    {
        if (_watches.Count == 0 || !_watches.TryGetValue(blockRoot, out ColumnWatch? watch) || !watch.Missing.Remove(column) || watch.Missing.Count > 0)
        {
            return null;
        }

        _watches.Remove(blockRoot);
        Volatile.Write(ref _watchCount, _watches.Count);
        return watch.Wake;
    }

    private bool HasPendingGloas(Hash256 blockRoot, ulong column)
    {
        lock (_pendingLock)
        {
            return _pendingByKey.ContainsKey((blockRoot, column));
        }
    }

    private sealed class ColumnWatch(HashSet<ulong> missing, Action wake)
    {
        public HashSet<ulong> Missing { get; } = missing;

        public Action Wake { get; } = wake;
    }

    public bool TryGet(Hash256 blockRoot, ulong column, out DataColumnSidecar? sidecar)
    {
        lock (_servedLock)
        {
            return _byRootAndColumn.TryGet((blockRoot, column), out sidecar);
        }
    }

    /// <summary>Stores a verified Gloas sidecar under its own <c>beacon_block_root</c> and <c>index</c>, and drops every pending candidate for them.</summary>
    /// <exception cref="ArgumentException"><paramref name="sidecar"/> names no beacon block root.</exception>
    public void AddGloas(DataColumnSidecarGloas sidecar)
    {
        Hash256 blockRoot = BlockRootOf(sidecar);
        Action? wake;
        lock (_servedLock)
        {
            MarkGiven(sidecar.Slot);
            _gloasByRootAndColumn.Set((blockRoot, sidecar.Index), sidecar.Slot, sidecar);
            wake = TakeWakeOnArrival(blockRoot, sidecar.Index);
        }

        lock (_pendingLock)
        {
            if (_pendingByKey.Remove((blockRoot, sidecar.Index), out List<DataColumnSidecarGloas>? candidates))
            {
                _pendingCount -= candidates.Count;
            }
        }

        wake?.Invoke();
    }

    public bool TryGetGloas(Hash256 blockRoot, ulong column, [NotNullWhen(true)] out DataColumnSidecarGloas? sidecar)
    {
        lock (_servedLock)
        {
            return _gloasByRootAndColumn.TryGet((blockRoot, column), out sidecar);
        }
    }

    /// <summary>
    /// Parks an unverified Gloas sidecar whose block is not yet known as a candidate for its own
    /// <c>beacon_block_root</c> and <c>index</c>, unless that key or the pool is full.
    /// </summary>
    /// <param name="sidecar">The unverified sidecar.</param>
    /// <param name="currentSlot">The wall-clock slot, which sets the retention window.</param>
    /// <returns>Whether the sidecar was parked.</returns>
    /// <remarks>
    /// The source of an unverified sidecar is unknown, so no arrival can displace an earlier one: a key keeps
    /// its first <see cref="MaxPendingGloasCandidatesPerKey"/> candidates, and a full pool refuses new ones.
    /// A candidate is kept until the end of the slot after its own, so a flood holds space only while it lasts.
    /// A flood can still deny parking at no cost to its sender, so a sampled column missing when its block arrives can be recovered only by a DataColumnSidecarsByRoot request.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="sidecar"/> names no beacon block root.</exception>
    public bool AddPendingGloas(DataColumnSidecarGloas sidecar, ulong currentSlot)
    {
        (Hash256 BlockRoot, ulong Column) key = (BlockRootOf(sidecar), sidecar.Index);

        lock (_pendingLock)
        {
            PruneStalePending(currentSlot);
            if (IsStale(sidecar, currentSlot) || _pendingCount >= _maxPendingGloas)
            {
                return false;
            }

            if (!_pendingByKey.TryGetValue(key, out List<DataColumnSidecarGloas>? candidates))
            {
                candidates = [];
                _pendingByKey[key] = candidates;
            }
            else if (candidates.Count >= MaxPendingGloasCandidatesPerKey)
            {
                return false;
            }

            candidates.Add(sidecar);
            _pendingCount++;
        }

        if (Volatile.Read(ref _watchCount) == 0)
        {
            return true;
        }

        // After the pending lock is released: watches are guarded by the served lock, which registration takes before the pending one.
        Action? wake;
        lock (_servedLock)
        {
            wake = TakeWakeOnArrival(key.BlockRoot, key.Column);
        }

        wake?.Invoke();
        return true;
    }

    /// <summary>A snapshot of the parked, unverified Gloas candidates for a block root and column, in arrival order.</summary>
    /// <remarks>The caller must verify each against the block and match its <c>slot</c> to the block's before use.</remarks>
    public DataColumnSidecarGloas[] GetPendingGloas(Hash256 blockRoot, ulong column)
    {
        lock (_pendingLock)
        {
            return _pendingByKey.TryGetValue((blockRoot, column), out List<DataColumnSidecarGloas>? candidates) ? [.. candidates] : [];
        }
    }

    /// <summary>Drops a parked candidate that failed verification against its block; a no-op if it is no longer parked.</summary>
    public void DiscardPendingGloas(DataColumnSidecarGloas sidecar)
    {
        if (sidecar.BeaconBlockRoot is not { } blockRoot) return;

        lock (_pendingLock)
        {
            if (!_pendingByKey.TryGetValue((blockRoot, sidecar.Index), out List<DataColumnSidecarGloas>? candidates)) return;

            for (int i = 0; i < candidates.Count; i++)
            {
                if (ReferenceEquals(candidates[i], sidecar))
                {
                    candidates.RemoveAt(i);
                    _pendingCount--;
                    if (candidates.Count == 0)
                    {
                        _pendingByKey.Remove((blockRoot, sidecar.Index));
                    }

                    return;
                }
            }
        }
    }

    /// <summary>The pending candidates' current total, bounded by <see cref="MaxPendingGloasSidecars"/>; for tests and diagnostics.</summary>
    internal int PendingGloasCount
    {
        get
        {
            lock (_pendingLock)
            {
                return _pendingCount;
            }
        }
    }

    private void PruneStalePending(ulong currentSlot)
    {
        if (currentSlot <= _pendingPrunedAtSlot)
        {
            return;
        }

        _pendingPrunedAtSlot = currentSlot;
        List<(Hash256 BlockRoot, ulong Column)>? emptied = null;
        foreach (KeyValuePair<(Hash256 BlockRoot, ulong Column), List<DataColumnSidecarGloas>> entry in _pendingByKey)
        {
            for (int i = entry.Value.Count - 1; i >= 0; i--)
            {
                if (IsStale(entry.Value[i], currentSlot))
                {
                    entry.Value.RemoveAt(i);
                    _pendingCount--;
                }
            }

            if (entry.Value.Count == 0)
            {
                (emptied ??= []).Add(entry.Key);
            }
        }

        if (emptied is not null)
        {
            foreach ((Hash256 BlockRoot, ulong Column) key in emptied)
            {
                _pendingByKey.Remove(key);
            }
        }
    }

    // A candidate outlives its own slot by one, so a block that arrives late in the next slot still finds it.
    private static bool IsStale(DataColumnSidecarGloas sidecar, ulong currentSlot) => sidecar.Slot < currentSlot && currentSlot - sidecar.Slot > 1;

    // A sidecar older than the first one given does not make the slots between them complete.
    private void MarkGiven(ulong slot) => _firstGivenSlot ??= slot;

    private static Hash256 BlockRootOf(DataColumnSidecarGloas sidecar) =>
        sidecar.BeaconBlockRoot ?? throw new ArgumentException("A Gloas data column sidecar must name its beacon block root", nameof(sidecar));

    /// <summary>
    /// Sidecars by (block root, column): at most <paramref name="capacity"/> retained lowest slot first, oldest first within a
    /// slot, plus the <paramref name="capacity"/> most recently given; not thread-safe.
    /// </summary>
    /// <remarks>
    /// The retained set is what <see cref="IncompleteBelow"/> describes. The recent set holds a sidecar the retained set
    /// refuses, so a verified range-synced column below every held slot is still found when its block is imported.
    /// </remarks>
    private sealed class SlotOrderedSidecars<TSidecar>(int capacity) where TSidecar : class
    {
        private readonly int _capacity = capacity >= 1 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The pool must hold at least one sidecar");
        private readonly Dictionary<(Hash256 BlockRoot, ulong Column), (TSidecar Sidecar, ulong Slot, LinkedListNode<(Hash256 BlockRoot, ulong Column)> Node)> _entries = [];
        private readonly SortedDictionary<ulong, LinkedList<(Hash256 BlockRoot, ulong Column)>> _keysBySlot = [];
        private readonly LruCache<(Hash256 BlockRoot, ulong Column), TSidecar> _recent = new(capacity, "recent data column sidecars");

        /// <summary>One past the highest slot that lost or was refused a retained sidecar; every slot from here up is retained completely.</summary>
        public ulong IncompleteBelow { get; private set; }

        public int SlotCount => _keysBySlot.Count;

        public void Set((Hash256 BlockRoot, ulong Column) key, ulong slot, TSidecar sidecar)
        {
            _recent.Set(key, sidecar);
            if (_entries.TryGetValue(key, out (TSidecar Sidecar, ulong Slot, LinkedListNode<(Hash256 BlockRoot, ulong Column)> Node) held))
            {
                if (held.Slot == slot)
                {
                    _entries[key] = (sidecar, slot, held.Node);
                    return;
                }

                Remove(key, held.Slot);
            }

            if (_entries.Count >= _capacity && !TryEvictAtOrBelow(slot))
            {
                MarkIncomplete(slot);
                return;
            }

            if (!_keysBySlot.TryGetValue(slot, out LinkedList<(Hash256 BlockRoot, ulong Column)>? keys))
            {
                keys = [];
                _keysBySlot[slot] = keys;
            }

            _entries[key] = (sidecar, slot, keys.AddLast(key));
        }

        public bool TryGet((Hash256 BlockRoot, ulong Column) key, [NotNullWhen(true)] out TSidecar? sidecar)
        {
            if (_entries.TryGetValue(key, out (TSidecar Sidecar, ulong Slot, LinkedListNode<(Hash256 BlockRoot, ulong Column)> Node) held))
            {
                sidecar = held.Sidecar;
                return true;
            }

            return _recent.TryGet(key, out sidecar!) && sidecar is not null;
        }

        // A slot below the lowest held one would itself be the next eviction, so it is refused instead of displacing a higher slot.
        private bool TryEvictAtOrBelow(ulong slot)
        {
            foreach (KeyValuePair<ulong, LinkedList<(Hash256 BlockRoot, ulong Column)>> lowest in _keysBySlot)
            {
                if (slot < lowest.Key) return false;

                Remove(lowest.Value.First!.Value, lowest.Key);
                return true;
            }

            return false;
        }

        private void Remove((Hash256 BlockRoot, ulong Column) key, ulong slot)
        {
            LinkedList<(Hash256 BlockRoot, ulong Column)> keys = _keysBySlot[slot];
            keys.Remove(_entries[key].Node);
            _entries.Remove(key);
            if (keys.Count == 0)
            {
                _keysBySlot.Remove(slot);
            }

            MarkIncomplete(slot);
        }

        private void MarkIncomplete(ulong slot) => IncompleteBelow = Math.Max(IncompleteBelow, slot == ulong.MaxValue ? slot : slot + 1);
    }
}
