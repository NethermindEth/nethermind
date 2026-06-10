// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.State.Flat.PersistedSnapshots;

namespace Nethermind.State.Flat;

/// <summary>
/// Parent-edge kinds of the two-tier snapshot DAG. The first four values are ordered by
/// <see cref="SnapshotGraphWalker.ParentCursor"/>'s expansion priority
/// (in-RAM-tier-first / widest-first).
/// </summary>
internal enum SnapshotEdge
{
    /// <summary>In-memory compacted — widest in-RAM hop, no disk read.</summary>
    InMemoryCompacted,
    /// <summary>In-memory base — narrow in-RAM hop, no disk read.</summary>
    InMemoryBase,
    /// <summary>Persisted compacted — &gt;CompactSize merges and the CompactSize persistable.</summary>
    PersistedCompacted,
    /// <summary>Persisted base — sub-CompactSize, narrowest persisted hop.</summary>
    PersistedBase,
    /// <summary>The CompactSize-wide persistable. Never expanded by
    /// <see cref="SnapshotGraphWalker.ParentCursor"/>; only leased through explicit
    /// <see cref="SnapshotGraphWalker.TryLeaseParent"/> calls (see <see cref="PersistenceManager"/>).</summary>
    PersistedPersistable,
}

/// <summary>
/// Edge-enumeration seam shared by every walk over the two-tier snapshot DAG: given a
/// <see cref="StateId"/> node, leases the snapshot backing one of its parent (<c>From</c>) edges.
/// </summary>
/// <remarks>
/// Callers own every lease handed out and must dispose it on all paths (or transfer ownership);
/// a leaked lease pins the snapshot, a double release is a use-after-free.
/// </remarks>
internal readonly struct SnapshotGraphWalker(ISnapshotRepository snapshots, IPersistedSnapshotRepository persisted)
{
    /// <summary>
    /// Tries to lease the snapshot ending at <paramref name="to"/> on the given edge kind,
    /// handing back the lease and the parent node it chains from.
    /// </summary>
    public bool TryLeaseParent(in StateId to, SnapshotEdge edge, [NotNullWhen(true)] out IDisposable? snapshot, out StateId from)
    {
        switch (edge)
        {
            case SnapshotEdge.InMemoryCompacted:
                if (snapshots.TryLeaseCompactedState(to, out Snapshot? inMemoryCompacted))
                {
                    (snapshot, from) = (inMemoryCompacted, inMemoryCompacted.From);
                    return true;
                }
                break;
            case SnapshotEdge.InMemoryBase:
                if (snapshots.TryLeaseState(to, out Snapshot? inMemoryBase))
                {
                    (snapshot, from) = (inMemoryBase, inMemoryBase.From);
                    return true;
                }
                break;
            case SnapshotEdge.PersistedCompacted:
                if (persisted.TryLeaseCompactedSnapshotTo(to, out PersistedSnapshot? persistedCompacted))
                {
                    (snapshot, from) = (persistedCompacted, persistedCompacted.From);
                    return true;
                }
                break;
            case SnapshotEdge.PersistedBase:
                if (persisted.TryLeaseSnapshotTo(to, out PersistedSnapshot? persistedBase))
                {
                    (snapshot, from) = (persistedBase, persistedBase.From);
                    return true;
                }
                break;
            case SnapshotEdge.PersistedPersistable:
                if (persisted.TryLeasePersistableCompactedSnapshotTo(to, out PersistedSnapshot? persistable))
                {
                    (snapshot, from) = (persistable, persistable.From);
                    return true;
                }
                break;
        }

        (snapshot, from) = (null, default);
        return false;
    }

    /// <summary>
    /// Starts a priority-ordered expansion of <paramref name="to"/>'s parent edges:
    /// <see cref="SnapshotEdge.InMemoryCompacted"/>, <see cref="SnapshotEdge.InMemoryBase"/>,
    /// <see cref="SnapshotEdge.PersistedCompacted"/>, <see cref="SnapshotEdge.PersistedBase"/>.
    /// </summary>
    /// <param name="to">The node whose parent edges are expanded.</param>
    /// <param name="fromPersistedEdge">Whether <paramref name="to"/> was itself reached over a
    /// persisted edge. Persisted snapshots only chain back to other persisted snapshots by
    /// construction, so the in-memory edges are guaranteed misses and are skipped — the
    /// once-persisted-stays-persisted gate.</param>
    /// <param name="includePersisted">When <see langword="false"/>, only the in-memory edges are
    /// expanded (the persisted tier is not walked).</param>
    public ParentCursor EnumerateParents(in StateId to, bool fromPersistedEdge, bool includePersisted) =>
        new(this, to, fromPersistedEdge, includePersisted);

    internal struct ParentCursor
    {
        private readonly SnapshotGraphWalker _walker;
        private readonly StateId _to;
        private readonly SnapshotEdge _end; // Exclusive.
        private SnapshotEdge _next;

        internal ParentCursor(in SnapshotGraphWalker walker, in StateId to, bool fromPersistedEdge, bool includePersisted)
        {
            _walker = walker;
            _to = to;
            _next = fromPersistedEdge ? SnapshotEdge.PersistedCompacted : SnapshotEdge.InMemoryCompacted;
            _end = includePersisted ? SnapshotEdge.PersistedPersistable : SnapshotEdge.PersistedCompacted;
        }

        /// <summary>
        /// Leases the next available parent edge in priority order. The caller owns the lease.
        /// </summary>
        public bool TryLeaseNext([NotNullWhen(true)] out IDisposable? snapshot, out StateId from, out bool viaPersistedEdge)
        {
            while (_next < _end)
            {
                SnapshotEdge edge = _next++;
                if (_walker.TryLeaseParent(_to, edge, out snapshot, out from))
                {
                    viaPersistedEdge = edge >= SnapshotEdge.PersistedCompacted;
                    return true;
                }
            }

            (snapshot, from, viaPersistedEdge) = (null, default, false);
            return false;
        }
    }
}
