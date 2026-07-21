// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat;

/// <summary>
/// Hook invoked by <see cref="PersistenceManager"/> as state is finalized to persistence, letting an implementation
/// capture the per-block changesets before they are pruned (used to build the archival history index).
/// </summary>
/// <remarks>
/// Contract the caller relies on: it runs after the flat state is persisted but <em>before</em> the per-block
/// snapshots are pruned and <em>before</em> the persisted-state barrier is published, so a reader never observes the
/// barrier ahead of what the hook captured. Exceptions are logged and swallowed by the caller — a capture failure
/// must not stall persistence — so an implementation must fail closed (leave the range uncaptured rather than
/// half-captured) and be idempotent, since a re-capture of the same range is expected after a restart.
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
