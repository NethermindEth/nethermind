// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Fork = Nethermind.State.Flat.Rust.Fork;

namespace Nethermind.Core.Test.Modules;

/// <summary>
/// <see cref="FlatDbManagerTestCompat"/> for the forked flat package <c>FlatDb.Backend=Rust</c> runs on: an empty-tree
/// state of a block the manager does not hold is read as <see cref="StateId.PreGenesis"/>.
/// </summary>
internal class ForkFlatDbManagerTestCompat(Fork.IFlatDbManager flatDbManager) : Fork.IFlatDbManager
{
    public Fork.SnapshotBundle GatherSnapshotBundle(in StateId stateId, ResourcePool.Usage usage) =>
        GatherSnapshotBundle(stateId, usage, filterInMemorySlotReads: false);

    public Fork.SnapshotBundle GatherSnapshotBundle(in StateId stateId, ResourcePool.Usage usage, bool filterInMemorySlotReads) =>
        flatDbManager.GatherSnapshotBundle(NormalizeState(stateId), usage, filterInMemorySlotReads);

    public Fork.ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId stateId) => flatDbManager.GatherReadOnlySnapshotBundle(NormalizeState(stateId));

    public Fork.ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId stateId, ReaderFlags readerFlags) => flatDbManager.GatherReadOnlySnapshotBundle(NormalizeState(stateId), readerFlags);

    public bool HasStateForBlock(in StateId stateId)
    {
        if (stateId.StateRoot == Keccak.EmptyTreeHash) return true;
        return flatDbManager.HasStateForBlock(stateId);
    }

    public bool HasStateForBlock(in StateId stateId, ResourcePool.Usage usage) =>
        stateId.StateRoot == Keccak.EmptyTreeHash || flatDbManager.HasStateForBlock(stateId, usage);

    private StateId NormalizeState(StateId stateId)
    {
        if (stateId.StateRoot == Keccak.EmptyTreeHash && stateId.BlockNumber != StateId.PreGenesis.BlockNumber &&
            !flatDbManager.HasStateForBlock(stateId))
            return StateId.PreGenesis;
        return stateId;
    }

    public void FlushCache(CancellationToken cancellationToken) => flatDbManager.FlushCache(cancellationToken);

    public void DropStateNotReachableFrom(in StateId head) => flatDbManager.DropStateNotReachableFrom(head);

    public void AddSnapshot(Fork.Snapshot snapshot, TransientResource transientResource) => flatDbManager.AddSnapshot(snapshot, transientResource);
}
