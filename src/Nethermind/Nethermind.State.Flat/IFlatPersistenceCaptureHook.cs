// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat;

/// <summary>
/// Hook invoked by <see cref="PersistenceManager"/> as state is finalized to persistence, letting an implementation
/// capture the per-block changesets before they are pruned (used to build the archival history index).
/// </summary>
/// <remarks>
/// Contract: runs <em>before</em> the flat persist, the prune, and the barrier publish — the flat head must never
/// advance past durable history. Captured data must be crash-durable when the call returns. An exception aborts
/// the persist iteration and the range is retried, so implementations must be idempotent under re-capture and
/// must not throw for conditions that can never resolve (a permanent gap), or persistence would stall forever.
/// </remarks>
public interface IFlatPersistenceCaptureHook
{
    /// <summary>
    /// Captures every not-yet-captured block on <paramref name="persistedHead"/>'s chain, up to and including it,
    /// leasing the per-block snapshots from <paramref name="snapshotRepository"/>.
    /// </summary>
    /// <param name="persistedHead">The state just persisted; capture proceeds down its <see cref="Snapshot.From"/> chain.</param>
    /// <param name="snapshotRepository">Source of the per-block snapshots to capture; leases must be disposed.</param>
    void CaptureUpTo(in StateId persistedHead, ISnapshotRepository snapshotRepository);
}
