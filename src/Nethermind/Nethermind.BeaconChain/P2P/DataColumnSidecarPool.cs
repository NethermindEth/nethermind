// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
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
/// is only replaced once a sidecar for the new canonical block at that slot is added.
/// Gloas sidecars (<see cref="DataColumnSidecarGloas"/>) sit in their own maps with the same bounds. A
/// Gloas sidecar whose block is not yet known can be parked as pending: pending sidecars are never
/// returned by the served lookups, and move to the served map only through <see cref="AddGloas"/>
/// once verified against their block's bid.
/// </remarks>
public sealed class DataColumnSidecarPool(int capacity = 1 << 14)
{
    private readonly LruCache<(Hash256 BlockRoot, ulong Column), DataColumnSidecar> _byRootAndColumn = new(capacity, "data column sidecars");
    private readonly LruCache<ulong, Hash256> _rootBySlot = new(capacity, "data column sidecars by slot");
    private readonly LruCache<(Hash256 BlockRoot, ulong Column), DataColumnSidecarGloas> _gloasByRootAndColumn = new(capacity, "gloas data column sidecars");
    private readonly LruCache<ulong, Hash256> _gloasRootBySlot = new(capacity, "gloas data column sidecars by slot");
    // Pending sidecars are unverified and peer-supplied, so they get a smaller bound than the served maps.
    private readonly LruCache<(Hash256 BlockRoot, ulong Column), DataColumnSidecarGloas> _gloasPending = new(Math.Min(capacity, MaxPendingGloasSidecars), "pending gloas data column sidecars");

    /// <summary>The most unverified Gloas sidecars <see cref="AddPendingGloas"/> holds at once.</summary>
    public const int MaxPendingGloasSidecars = 1 << 10;

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

    /// <summary>The Gloas slot-to-root index's current entry count, bounded by <paramref name="capacity"/>; for tests and diagnostics.</summary>
    internal int GloasSlotIndexCount => _gloasRootBySlot.Count;

    /// <summary>Stores a verified Gloas sidecar under its own <c>beacon_block_root</c>, <c>index</c> and <c>slot</c>, and drops any pending copy.</summary>
    /// <exception cref="ArgumentException"><paramref name="sidecar"/> names no beacon block root.</exception>
    public void AddGloas(DataColumnSidecarGloas sidecar)
    {
        Hash256 blockRoot = BlockRootOf(sidecar);
        _gloasByRootAndColumn.Set((blockRoot, sidecar.Index), sidecar);
        _gloasRootBySlot.Set(sidecar.Slot, blockRoot);
        _gloasPending.Delete((blockRoot, sidecar.Index));
    }

    public bool TryGetGloas(Hash256 blockRoot, ulong column, [NotNullWhen(true)] out DataColumnSidecarGloas? sidecar) =>
        _gloasByRootAndColumn.TryGet((blockRoot, column), out sidecar);

    public bool TryGetGloas(ulong slot, ulong column, [NotNullWhen(true)] out DataColumnSidecarGloas? sidecar)
    {
        if (_gloasRootBySlot.TryGet(slot, out Hash256? root))
        {
            return TryGetGloas(root, column, out sidecar);
        }

        sidecar = null;
        return false;
    }

    /// <summary>Parks an unverified Gloas sidecar whose block is not yet known, keyed by its own <c>beacon_block_root</c> and <c>index</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="sidecar"/> names no beacon block root.</exception>
    public void AddPendingGloas(DataColumnSidecarGloas sidecar) =>
        _gloasPending.Set((BlockRootOf(sidecar), sidecar.Index), sidecar);

    /// <summary>Looks up a parked, unverified Gloas sidecar; the caller must verify it and match its <c>slot</c> to the block's before use.</summary>
    public bool TryGetPendingGloas(Hash256 blockRoot, ulong column, [NotNullWhen(true)] out DataColumnSidecarGloas? sidecar) =>
        _gloasPending.TryGet((blockRoot, column), out sidecar);

    private static Hash256 BlockRootOf(DataColumnSidecarGloas sidecar) =>
        sidecar.BeaconBlockRoot ?? throw new ArgumentException("A Gloas data column sidecar must name its beacon block root", nameof(sidecar));
}
