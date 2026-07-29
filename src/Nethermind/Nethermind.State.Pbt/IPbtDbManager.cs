// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.State.Pbt;

/// <summary>Receiver of snapshots sealed at block commit, taking ownership of the snapshot's initial lease.</summary>
public interface IPbtCommitTarget
{
    void AddSnapshot(PbtSnapshot snapshot);
}

/// <summary>Top-level orchestrator of the PBT state: hands out bundles, receives committed snapshots and drives background persistence.</summary>
public interface IPbtDbManager : IPbtCommitTarget
{
    /// <summary>Assembles the shared, immutable view of <paramref name="stateId"/>, or null when that state is not available.</summary>
    /// <remarks>Rents nothing from the pool: a caller that only reads should prefer this.</remarks>
    PbtReadOnlySnapshotBundle? TryGatherReadOnlyBundle(in StateId stateId);

    /// <inheritdoc cref="TryGatherReadOnlyBundle"/>
    /// <exception cref="InvalidOperationException">The state is not available.</exception>
    PbtReadOnlySnapshotBundle GatherReadOnlyBundle(in StateId stateId) =>
        TryGatherReadOnlyBundle(stateId) ?? throw new InvalidOperationException($"State {stateId} is not available");

    /// <summary>Assembles a writable bundle able to serve reads at <paramref name="stateId"/>, or null when that state is not available.</summary>
    /// <param name="usage">
    /// Pool category for the bundle's write buffer and the layers it seals. Chosen by the caller
    /// rather than inferred: an override scope gathers a writable bundle that is still not main block
    /// processing, so there is nothing about the bundle itself to infer it from.
    /// </param>
    PbtSnapshotBundle? TryGatherBundle(in StateId stateId, PbtResourcePool.Usage usage);

    /// <inheritdoc cref="TryGatherBundle"/>
    /// <exception cref="InvalidOperationException">The state is not available.</exception>
    PbtSnapshotBundle GatherBundle(in StateId stateId, PbtResourcePool.Usage usage) =>
        TryGatherBundle(stateId, usage) ?? throw new InvalidOperationException($"State {stateId} is not available");

    bool HasStateForBlock(in StateId stateId);

    /// <summary>Synchronously persists everything up to the committed head, e.g. after genesis processing.</summary>
    void FlushCache(CancellationToken cancellationToken);
}
