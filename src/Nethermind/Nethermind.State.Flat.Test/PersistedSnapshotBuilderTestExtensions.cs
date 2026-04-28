// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.PersistedSnapshots;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Test-only convenience methods for <see cref="PersistedSnapshotBuilder"/>.
/// These allocate output buffers internally, which production code avoids.
/// </summary>
internal static class PersistedSnapshotBuilderTestExtensions
{
    public static byte[] Build(Snapshot snapshot)
    {
        int estimatedSize = PersistedSnapshotBuilder.EstimateSize(snapshot);
        using PooledByteBufferWriter pooled = new(estimatedSize);
        PersistedSnapshotBuilder.Build(snapshot, ref pooled.GetWriter());
        return pooled.WrittenSpan.ToArray();
    }

    public static byte[] MergeSnapshots(PersistedSnapshotList snapshots) =>
        NWayMergeSnapshots(snapshots);

    public static byte[] NWayMergeSnapshots(PersistedSnapshotList snapshots)
    {
        if (snapshots.Count == 0) throw new ArgumentException("Cannot merge empty snapshot list");
        if (snapshots.Count == 1) return snapshots[0].GetSpan().ToArray();

        int totalSize = 0;
        for (int i = 0; i < snapshots.Count; i++) totalSize += snapshots[i].Size;
        totalSize += 4096;

        using PooledByteBufferWriter pooled = new(totalSize);
        PersistedSnapshotBuilder.NWayMergeSnapshotsNoTrie(snapshots, ref pooled.GetWriter());
        return pooled.WrittenSpan.ToArray();
    }

    public static byte[] MergeSnapshotsNoTrie(PersistedSnapshotList snapshots)
    {
        int totalSize = 0;
        for (int i = 0; i < snapshots.Count; i++) totalSize += snapshots[i].Size;
        totalSize += 4096;

        using PooledByteBufferWriter pooled = new(totalSize);
        PersistedSnapshotBuilder.NWayMergeSnapshotsNoTrie(snapshots, ref pooled.GetWriter());
        return pooled.WrittenSpan.ToArray();
    }
}
