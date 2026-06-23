// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.State.Flat.Io;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Allocates output buffers internally, which production code avoids.
/// </summary>
internal static class PersistedSnapshotBuilderTestExtensions
{
    /// <summary>
    /// The caller must keep <paramref name="blobs"/> alive across the test fixture so that a
    /// <see cref="PersistedSnapshot"/> constructed from the returned bytes can lease the blob
    /// file via the same manager — mirroring how production wires <c>BlobArenaManager</c> as
    /// a long-lived shared component.
    /// </summary>
    public static byte[] Build(Snapshot snapshot, BlobArenaManager blobs)
    {
        int estimatedSize = checked((int)PersistedSnapshotBuilder.EstimateSize(snapshot));
        using PooledByteBufferWriter pooled = new(estimatedSize);
        using BlobArenaWriter blobWriter = blobs.CreateWriter(estimatedSize);
        using Nethermind.State.Flat.Persistence.BloomFilter.BloomFilter bloom =
            Nethermind.State.Flat.Persistence.BloomFilter.BloomFilter.AlwaysTrue();
        PersistedSnapshotBuilder.Build<PooledByteBufferWriter.Writer>(
            snapshot, ref pooled.GetWriter(), blobWriter, bloom);
        blobWriter.Complete();
        return pooled.WrittenSpan.ToArray();
    }

    public static byte[] NWayMergeSnapshots(PersistedSnapshotList snapshots)
    {
        if (snapshots.Count == 0) throw new ArgumentException("Cannot merge empty snapshot list");
        if (snapshots.Count == 1)
        {
            using WholeReadSession session = snapshots[0].BeginWholeReadSession();
            return TestFixtureHelpers.ReadAll(session);
        }

        long totalSize = 0;
        for (int i = 0; i < snapshots.Count; i++) totalSize += snapshots[i].Size;
        totalSize += 4096;

        using PooledByteBufferWriter pooled = new(checked((int)totalSize));
        int n = snapshots.Count;
        using ArrayPoolList<WholeReadSession> sessionsList = new(n, n);
        WholeReadSession[] sessionArr = sessionsList.UnsafeGetInternalArray();
        try
        {
            for (int i = 0; i < n; i++)
                sessionArr[i] = snapshots[i].BeginWholeReadSession();
            PersistedSnapshotMerger.NWayMergeSnapshots<PooledByteBufferWriter.Writer, WholeReadSession, WholeReadSessionReader, NoOpPin>(
                sessionsList.AsSpan(), ref pooled.GetWriter(), bloom: Nethermind.State.Flat.Persistence.BloomFilter.BloomFilter.AlwaysTrue());
        }
        finally
        {
            for (int i = 0; i < n; i++) sessionArr[i]?.Dispose();
        }
        return pooled.WrittenSpan.ToArray();
    }
}
