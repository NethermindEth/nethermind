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
/// epochs): entries are evicted under capacity pressure like any LRU cache, with no epoch-based
/// retention guarantee. A durable, retention-window-honoring store belongs with <c>Storage/</c>.
/// The slot-to-root index is itself LRU-bounded to the same capacity: an unbounded map keyed by slot
/// would otherwise grow for as long as the process runs, one entry per slot forever. It is
/// last-write-wins and untracked against reorgs: a sidecar added for a slot that is later reorged out
/// is only replaced once a sidecar for the new canonical block at that slot is added, so by-range
/// serving never reads it and looks up the canonical root's sidecars by (root, column) instead.
/// Gloas sidecars (<see cref="DataColumnSidecarGloas"/>) sit in their own map with the same bound and
/// no slot index: a slot can carry competing blocks, so by-range serving must resolve the canonical
/// root first and look up by (root, column). A Gloas sidecar whose block is not yet known can be
/// parked as pending: pending sidecars are never returned by the served lookups, and move to the
/// served map only through <see cref="AddGloas"/> once verified against their block's bid.
/// </remarks>
public sealed class DataColumnSidecarPool(int capacity = 1 << 14)
{
    private readonly LruCache<(Hash256 BlockRoot, ulong Column), DataColumnSidecar> _byRootAndColumn = new(capacity, "data column sidecars");
    private readonly LruCache<ulong, Hash256> _rootBySlot = new(capacity, "data column sidecars by slot");
    private readonly LruCache<(Hash256 BlockRoot, ulong Column), DataColumnSidecarGloas> _gloasByRootAndColumn = new(capacity, "gloas data column sidecars");
    // Pending sidecars are unverified and peer-supplied, so they get a smaller bound than the served maps.
    private readonly int _maxPendingGloas = Math.Min(capacity, MaxPendingGloasSidecars);
    private readonly Lock _pendingLock = new();
    private readonly Dictionary<(Hash256 BlockRoot, ulong Column), List<DataColumnSidecarGloas>> _pendingByKey = [];
    private int _pendingCount;
    private ulong _pendingPrunedAtSlot;

    /// <summary>The most unverified Gloas sidecars <see cref="AddPendingGloas"/> holds at once.</summary>
    public const int MaxPendingGloasSidecars = 1 << 10;

    /// <summary>The most unverified Gloas sidecars <see cref="AddPendingGloas"/> holds for one (root, column).</summary>
    /// <remarks>Availability runs a KZG batch per candidate, so this bounds that work per column; above one, so an earlier forgery cannot block the genuine sidecar alone.</remarks>
    public const int MaxPendingGloasCandidatesPerKey = 4;

    /// <summary>The slot-to-root index's current entry count, bounded by <paramref name="capacity"/>; for tests and diagnostics.</summary>
    internal int SlotIndexCount => _rootBySlot.Count;

    public void Add(Hash256 blockRoot, ulong slot, DataColumnSidecar sidecar)
    {
        _byRootAndColumn.Set((blockRoot, sidecar.Index), sidecar);
        _rootBySlot.Set(slot, blockRoot);
    }

    public bool TryGet(Hash256 blockRoot, ulong column, out DataColumnSidecar? sidecar) =>
        _byRootAndColumn.TryGet((blockRoot, column), out sidecar);

    public bool TryGet(ulong slot, ulong column, out DataColumnSidecar? sidecar)
    {
        if (_rootBySlot.TryGet(slot, out Hash256? root))
        {
            return TryGet(root, column, out sidecar);
        }

        sidecar = null;
        return false;
    }

    /// <summary>Stores a verified Gloas sidecar under its own <c>beacon_block_root</c> and <c>index</c>, and drops every pending candidate for them.</summary>
    /// <exception cref="ArgumentException"><paramref name="sidecar"/> names no beacon block root.</exception>
    public void AddGloas(DataColumnSidecarGloas sidecar)
    {
        Hash256 blockRoot = BlockRootOf(sidecar);
        _gloasByRootAndColumn.Set((blockRoot, sidecar.Index), sidecar);

        lock (_pendingLock)
        {
            if (_pendingByKey.Remove((blockRoot, sidecar.Index), out List<DataColumnSidecarGloas>? candidates))
            {
                _pendingCount -= candidates.Count;
            }
        }
    }

    public bool TryGetGloas(Hash256 blockRoot, ulong column, [NotNullWhen(true)] out DataColumnSidecarGloas? sidecar) =>
        _gloasByRootAndColumn.TryGet((blockRoot, column), out sidecar);

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
            return true;
        }
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

    private static Hash256 BlockRootOf(DataColumnSidecarGloas sidecar) =>
        sidecar.BeaconBlockRoot ?? throw new ArgumentException("A Gloas data column sidecar must name its beacon block root", nameof(sidecar));
}
