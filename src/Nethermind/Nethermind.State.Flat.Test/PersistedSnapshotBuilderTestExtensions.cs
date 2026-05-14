// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Storage;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Test-only convenience methods for <see cref="PersistedSnapshotBuilder"/>.
/// These allocate output buffers internally, which production code avoids.
/// </summary>
internal static class PersistedSnapshotBuilderTestExtensions
{
    /// <summary>
    /// Build a snapshot's HSST bytes, writing trie-node RLPs into <paramref name="blobs"/>.
    /// The caller owns <paramref name="blobs"/> across the test fixture so the
    /// <see cref="PersistedSnapshot"/> constructed from the returned bytes can lease the
    /// resulting blob file via the same manager — matching how production wires
    /// <c>BlobArenaManager</c> as a long-lived shared component.
    /// </summary>
    public static byte[] Build(Snapshot snapshot, BlobArenaManager blobs)
    {
        int estimatedSize = checked((int)PersistedSnapshotBuilder.EstimateSize(snapshot));
        using PooledByteBufferWriter pooled = new(estimatedSize);
        using BlobArenaWriter blobWriter = blobs.CreateWriter(estimatedSize);
        PersistedSnapshotBuilder.Build<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin>(
            snapshot, ref pooled.GetWriter(), blobWriter);
        blobWriter.Complete();
        return pooled.WrittenSpan.ToArray();
    }

    public static byte[] MergeSnapshots(PersistedSnapshotList snapshots) =>
        NWayMergeSnapshots(snapshots);

    public static byte[] NWayMergeSnapshots(PersistedSnapshotList snapshots)
    {
        if (snapshots.Count == 0) throw new ArgumentException("Cannot merge empty snapshot list");
        if (snapshots.Count == 1)
        {
            using WholeReadSession session = snapshots[0].BeginWholeReadSession();
            return session.AsSpanIntBounded().ToArray();
        }

        long totalSize = 0;
        for (int i = 0; i < snapshots.Count; i++) totalSize += snapshots[i].Size;
        totalSize += 4096;

        using PooledByteBufferWriter pooled = new(checked((int)totalSize));
        PersistedSnapshotMerger.NWayMergeSnapshots<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin>(
            snapshots, ref pooled.GetWriter());
        return pooled.WrittenSpan.ToArray();
    }
}
