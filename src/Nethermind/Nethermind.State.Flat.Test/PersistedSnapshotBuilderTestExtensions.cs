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
    public static byte[] Build(Snapshot snapshot)
    {
        int estimatedSize = checked((int)PersistedSnapshotBuilder.EstimateSize(snapshot));
        using PooledByteBufferWriter pooled = new(estimatedSize);
        string tempDir = Path.Combine(Path.GetTempPath(), "nm-blobtest-" + Guid.NewGuid().ToString("N"));
        try
        {
            using BlobArenaManager blobs = new(tempDir, 4L * 1024 * 1024, ArenaReservationTags.BlobSmall);
            using BlobArenaWriter blobWriter = blobs.CreateWriter(estimatedSize, "TestBlob");
            PersistedSnapshotBuilder.Build<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin>(
                snapshot, ref pooled.GetWriter(), blobWriter);
            blobWriter.Complete();
            return pooled.WrittenSpan.ToArray();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
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

        HashSet<ushort> referencedIds = new();
        for (int i = 0; i < snapshots.Count; i++)
        {
            foreach (ushort id in snapshots[i].ReferencedBlobArenaIds)
                referencedIds.Add(id);
        }

        long totalSize = 0;
        for (int i = 0; i < snapshots.Count; i++) totalSize += snapshots[i].Size;
        totalSize += 4096;

        using PooledByteBufferWriter pooled = new(checked((int)totalSize));
        PersistedSnapshotBuilder.NWayMergeSnapshots<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin>(
            snapshots, ref pooled.GetWriter(), referencedIds);
        return pooled.WrittenSpan.ToArray();
    }
}
