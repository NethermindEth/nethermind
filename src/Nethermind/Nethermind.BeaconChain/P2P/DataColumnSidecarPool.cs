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
/// is only replaced once a sidecar for the new canonical block at that slot is added.
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
    private readonly Dictionary<(Hash256 BlockRoot, ulong Column), List<PendingCandidate>> _pendingByKey = [];
    private readonly Dictionary<string, LinkedList<PendingCandidate>> _pendingByPeer = [];
    private int _pendingCount;
    private long _pendingSequence;

    /// <summary>The most unverified Gloas sidecars <see cref="AddPendingGloas"/> holds at once, across all peers.</summary>
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

    /// <summary>Stores a verified Gloas sidecar under its own <c>beacon_block_root</c> and <c>index</c>, and drops every pending candidate for them.</summary>
    /// <exception cref="ArgumentException"><paramref name="sidecar"/> names no beacon block root.</exception>
    public void AddGloas(DataColumnSidecarGloas sidecar)
    {
        Hash256 blockRoot = BlockRootOf(sidecar);
        _gloasByRootAndColumn.Set((blockRoot, sidecar.Index), sidecar);

        lock (_pendingLock)
        {
            if (_pendingByKey.Remove((blockRoot, sidecar.Index), out List<PendingCandidate>? candidates))
            {
                foreach (PendingCandidate candidate in candidates)
                {
                    UnlinkFromPeer(candidate);
                }
            }
        }
    }

    public bool TryGetGloas(Hash256 blockRoot, ulong column, [NotNullWhen(true)] out DataColumnSidecarGloas? sidecar) =>
        _gloasByRootAndColumn.TryGet((blockRoot, column), out sidecar);

    /// <summary>
    /// Parks an unverified Gloas sidecar whose block is not yet known as <paramref name="peerId"/>'s candidate
    /// for its own <c>beacon_block_root</c> and <c>index</c>, replacing that peer's earlier candidate for them.
    /// </summary>
    /// <param name="sidecar">The unverified sidecar.</param>
    /// <param name="peerId">The peer the sidecar came from.</param>
    /// <remarks>
    /// Candidates from different peers for one (root, column) are all kept, so a forgery can neither
    /// replace a valid sidecar queued earlier nor block one queued later. When the pool is full, the
    /// oldest candidate of the peer holding the most is evicted, so a peer that floods the pool evicts
    /// its own candidates before any other peer's.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="sidecar"/> names no beacon block root.</exception>
    public void AddPendingGloas(DataColumnSidecarGloas sidecar, string peerId)
    {
        ArgumentNullException.ThrowIfNull(peerId);
        (Hash256 BlockRoot, ulong Column) key = (BlockRootOf(sidecar), sidecar.Index);

        lock (_pendingLock)
        {
            if (_pendingByKey.TryGetValue(key, out List<PendingCandidate>? candidates))
            {
                foreach (PendingCandidate candidate in candidates)
                {
                    if (candidate.PeerId == peerId)
                    {
                        candidate.Sidecar = sidecar;
                        candidate.Sequence = _pendingSequence++;
                        LinkedList<PendingCandidate> own = candidate.PeerNode.List!;
                        own.Remove(candidate.PeerNode);
                        own.AddLast(candidate.PeerNode);
                        return;
                    }
                }
            }

            if (_pendingCount >= _maxPendingGloas)
            {
                EvictFromLargestPeer(peerId);
            }

            // Eviction may have emptied and dropped this key's list.
            if (!_pendingByKey.TryGetValue(key, out candidates))
            {
                candidates = [];
                _pendingByKey[key] = candidates;
            }

            if (!_pendingByPeer.TryGetValue(peerId, out LinkedList<PendingCandidate>? byPeer))
            {
                byPeer = new LinkedList<PendingCandidate>();
                _pendingByPeer[peerId] = byPeer;
            }

            PendingCandidate added = new(key, peerId, sidecar, _pendingSequence++);
            candidates.Add(added);
            byPeer.AddLast(added.PeerNode);
            _pendingCount++;
        }
    }

    /// <summary>A snapshot of the parked, unverified Gloas candidates for a block root and column, at most one per source peer.</summary>
    /// <remarks>The caller must verify each against the block and match its <c>slot</c> to the block's before use.</remarks>
    public DataColumnSidecarGloas[] GetPendingGloas(Hash256 blockRoot, ulong column)
    {
        lock (_pendingLock)
        {
            if (!_pendingByKey.TryGetValue((blockRoot, column), out List<PendingCandidate>? candidates))
            {
                return [];
            }

            DataColumnSidecarGloas[] snapshot = new DataColumnSidecarGloas[candidates.Count];
            for (int i = 0; i < snapshot.Length; i++)
            {
                snapshot[i] = candidates[i].Sidecar;
            }

            return snapshot;
        }
    }

    /// <summary>Drops a parked candidate that failed verification against its block; a no-op if it is no longer parked.</summary>
    public void DiscardPendingGloas(DataColumnSidecarGloas sidecar)
    {
        if (sidecar.BeaconBlockRoot is not { } blockRoot) return;

        lock (_pendingLock)
        {
            if (!_pendingByKey.TryGetValue((blockRoot, sidecar.Index), out List<PendingCandidate>? candidates)) return;

            for (int i = 0; i < candidates.Count; i++)
            {
                if (ReferenceEquals(candidates[i].Sidecar, sidecar))
                {
                    RemoveCandidate(candidates[i], candidates, i);
                    return;
                }
            }
        }
    }

    /// <summary>The pending candidates' current total across all peers, bounded by <see cref="MaxPendingGloasSidecars"/>; for tests and diagnostics.</summary>
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

    private void EvictFromLargestPeer(string addingPeerId)
    {
        // The adding peer loses ties, so its flood never pushes out a peer holding as many candidates as it does.
        // Other ties go to the oldest candidate, so fresh peer ids cannot push out a candidate that just arrived.
        LinkedList<PendingCandidate>? adding = _pendingByPeer.GetValueOrDefault(addingPeerId);
        LinkedList<PendingCandidate>? largest = adding;
        foreach (LinkedList<PendingCandidate> byPeer in _pendingByPeer.Values)
        {
            if (largest is null
                || byPeer.Count > largest.Count
                || (byPeer.Count == largest.Count && largest != adding && byPeer.First!.Value.Sequence < largest.First!.Value.Sequence))
            {
                largest = byPeer;
            }
        }

        PendingCandidate oldest = largest!.First!.Value;
        List<PendingCandidate> candidates = _pendingByKey[oldest.Key];
        RemoveCandidate(oldest, candidates, candidates.IndexOf(oldest));
    }

    private void RemoveCandidate(PendingCandidate candidate, List<PendingCandidate> candidates, int index)
    {
        candidates.RemoveAt(index);
        if (candidates.Count == 0)
        {
            _pendingByKey.Remove(candidate.Key);
        }

        UnlinkFromPeer(candidate);
    }

    private void UnlinkFromPeer(PendingCandidate candidate)
    {
        LinkedList<PendingCandidate> byPeer = candidate.PeerNode.List!;
        byPeer.Remove(candidate.PeerNode);
        if (byPeer.Count == 0)
        {
            _pendingByPeer.Remove(candidate.PeerId);
        }

        _pendingCount--;
    }

    private static Hash256 BlockRootOf(DataColumnSidecarGloas sidecar) =>
        sidecar.BeaconBlockRoot ?? throw new ArgumentException("A Gloas data column sidecar must name its beacon block root", nameof(sidecar));

    private sealed class PendingCandidate
    {
        public PendingCandidate((Hash256 BlockRoot, ulong Column) key, string peerId, DataColumnSidecarGloas sidecar, long sequence)
        {
            Key = key;
            PeerId = peerId;
            Sidecar = sidecar;
            Sequence = sequence;
            PeerNode = new LinkedListNode<PendingCandidate>(this);
        }

        public (Hash256 BlockRoot, ulong Column) Key { get; }
        public string PeerId { get; }
        public DataColumnSidecarGloas Sidecar { get; set; }
        public long Sequence { get; set; }
        public LinkedListNode<PendingCandidate> PeerNode { get; }
    }
}
