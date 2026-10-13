// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.Rust;

/// <summary>
/// The original <see cref="IFlatDbManager"/> on <see cref="FlatDbBackend.Rust"/>, for the services of the original flat
/// package that only ask about states: it answers through the forked manager and refuses to hand out a bundle or to take
/// a snapshot, which only the forked package can read or write.
/// </summary>
internal sealed class FlatDbManagerGuard(Nethermind.State.Flat.Rust.Fork.IFlatDbManager flatDbManager) : IFlatDbManager
{
    public SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage) => throw Unsupported();

    public SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage, bool filterInMemorySlotReads) => throw Unsupported();

    public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock) => throw Unsupported();

    public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock, ReaderFlags readerFlags) => throw Unsupported();

    public void AddSnapshot(Snapshot snapshot, TransientResource transientResource) => throw Unsupported();

    public void FlushCache(CancellationToken cancellationToken) => flatDbManager.FlushCache(cancellationToken);

    public bool HasStateForBlock(in StateId stateId) => flatDbManager.HasStateForBlock(stateId);

    public bool HasStateForBlock(in StateId stateId, ResourcePool.Usage usage) => flatDbManager.HasStateForBlock(stateId, usage);

    public void DropStateNotReachableFrom(in StateId head) => flatDbManager.DropStateNotReachableFrom(head);

    private static NotSupportedException Unsupported() =>
        new($"FlatDb.Backend={FlatDbBackend.Rust} runs the forked flat package, whose {nameof(Nethermind.State.Flat.Rust.Fork.IFlatDbManager)} hands out the bundles and takes the snapshots.");
}
