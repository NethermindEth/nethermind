// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Snapshot;

namespace Nethermind.State.Pbt;

/// <summary>Receiver of snapshots sealed at block commit, taking ownership of the snapshot's initial lease and of the transient resource's owner lease.</summary>
public interface IPbtCommitTarget
{
    /// <param name="transientResource">The block's staged groups; the receiver must release its lease on every path.</param>
    void AddSnapshot(PbtSnapshot snapshot, PbtTransientResource transientResource);
}

/// <summary>Top-level orchestrator of the PBT state: hands out bundles, receives committed snapshots and drives background persistence.</summary>
public interface IPbtDbManager : IPbtCommitTarget
{
    /// <summary>Assembles the shared, immutable view of <paramref name="stateId"/>, or null when that state is not available.</summary>
    /// <remarks>Rents nothing from the pool: a caller that only reads should prefer this.</remarks>
    PbtReadOnlySnapshotBundle? TryGatherReadOnlyBundle(in StateId stateId);

    /// <inheritdoc cref="TryGatherReadOnlyBundle"/>
    /// <exception cref="StateNotRetainedException">The state is not available.</exception>
    PbtReadOnlySnapshotBundle GatherReadOnlyBundle(in StateId stateId) =>
        TryGatherReadOnlyBundle(stateId) ?? throw new StateNotRetainedException($"State {stateId} is not available");

    /// <summary>Assembles a writable bundle able to serve reads at <paramref name="stateId"/>, or null when that state is not available.</summary>
    /// <param name="usage">
    /// Pool category for the bundle's write buffer and the layers it seals. Chosen by the caller
    /// rather than inferred: an override scope gathers a writable bundle that is still not main block
    /// processing, so there is nothing about the bundle itself to infer it from.
    /// </param>
    PbtSnapshotBundle? TryGatherBundle(in StateId stateId, PbtResourcePool.Usage usage) => TryGatherBundle(stateId, new PbtSnapshotPooledList(1), usage, filterInMemorySlotReads: false);

    /// <summary>Assembles a writable bundle able to serve reads at the newest of <paramref name="localSnapshots"/>, or null when <paramref name="baseStateId"/> is not available.</summary>
    /// <param name="baseStateId">The state the oldest of <paramref name="localSnapshots"/> builds on.</param>
    /// <param name="localSnapshots">Leased snapshots kept outside this manager, oldest first; taken over on every path.</param>
    /// <param name="usage">Pool category for the bundle's write buffer and the layers it seals.</param>
    /// <param name="filterInMemorySlotReads">Serve slot reads through the in-memory layers' negative filter; for read-only execution only.</param>
    PbtSnapshotBundle? TryGatherBundle(in StateId baseStateId, PbtSnapshotPooledList localSnapshots, PbtResourcePool.Usage usage, bool filterInMemorySlotReads);

    /// <inheritdoc cref="TryGatherBundle(in StateId, PbtResourcePool.Usage)"/>
    /// <exception cref="StateNotRetainedException">The state is not available.</exception>
    PbtSnapshotBundle GatherBundle(in StateId stateId, PbtResourcePool.Usage usage) =>
        TryGatherBundle(stateId, usage) ?? throw new StateNotRetainedException($"State {stateId} is not available");

    bool HasStateForBlock(in StateId stateId);

    /// <summary>Prunes snapshots outside the head's ancestry without rewinding the durable base.</summary>
    void DropStateNotReachableFrom(in StateId head) => throw new NotSupportedException("Head rewind is not supported by this PBT manager.");

    /// <summary>Synchronously persists everything up to the committed head, e.g. after genesis processing.</summary>
    void FlushCache(CancellationToken cancellationToken);
}
