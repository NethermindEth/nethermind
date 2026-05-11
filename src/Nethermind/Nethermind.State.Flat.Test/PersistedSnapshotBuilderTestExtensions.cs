// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
        using MemoryArenaManager blobArena = new();
        BlobArenaCatalog blobCatalog = new(new Nethermind.Db.MemDb());
        using BlobArenaManager blobs = new(blobArena, blobCatalog, ArenaReservationTags.BlobSmall);
        using BlobArenaWriter blobWriter = blobs.CreateWriter(estimatedSize, "TestBlob");
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

        HashSet<int> referencedIds = new();
        for (int i = 0; i < snapshots.Count; i++)
        {
            foreach (int id in snapshots[i].ReferencedBlobArenaIds)
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
