// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.NodeBuffer;

namespace Nethermind.State.Pbt.Snapshot;

/// <summary>A sealed canonical EIP-8297 diff covering <see cref="From"/> to <see cref="To"/>.</summary>
public class PbtSnapshot(
    in StateId from,
    in StateId to,
    in ValueHash256 treeRoot,
    PbtSnapshotContent content,
    IPbtResourcePool resourcePool,
    PbtResourcePool.Usage usage) : RefCountingDisposable
{
    private PbtSnapshotPayloadSize? _payloadSize;
    private int _bloomPopulated;

    public StateId From { get; } = from;
    public StateId To { get; } = to;
    public ValueHash256 TreeRoot { get; } = treeRoot;
    /// <summary>The pointer of the snapshot's root group in its content's buffer, or 0 for the empty tree; only the root hash is persisted.</summary>
    public ulong RootGroupPointer { get; } = content.RootGroupPointer;
    public PbtSnapshotContent Content { get; } = content;

    public PbtSnapshotPayloadSize PayloadSize => _payloadSize ??= Content.GetPayloadSize();

    public bool TryLease() => TryAcquireLease();

    /// <summary>Adds the path key hashes of the groups written within this snapshot's range to the blooms of their buffer generations, once.</summary>
    /// <remarks>
    /// Called as the compactor first takes in a base snapshot, keeping it off the commit path. A path key hash missing from its
    /// bloom, of a snapshot not yet taken in or of a group no final root reaches, is fine: buffer compaction then keeps a superseded
    /// record, which only makes the compacted generation unnecessarily bigger.
    /// </remarks>
    public void PopulateBloom()
    {
        if (Content is not { GroupRecordBuffer: PbtGroupRecordBuffer buffer, NodeGroupWriteBoundary: ulong lowerBound } || Interlocked.Exchange(ref _bloomPopulated, 1) != 0) return;

        buffer.AddToBlooms(RootGroupPointer, lowerBound);
    }

    protected override void CleanUp() => resourcePool.ReturnSnapshotContent(usage, Content);
}
