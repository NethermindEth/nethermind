// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

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
            ArenaReservation reservation = snapshots[0].Reservation;
            ArenaByteReader reader = reservation.CreateReader();
            using NoOpPin pin = reader.PinBuffer(0, reservation.Size);
            return pin.Buffer.ToArray();
        }

        long totalSize = 0;
        for (int i = 0; i < snapshots.Count; i++) totalSize += snapshots[i].Size;
        totalSize += 4096;

        using PooledByteBufferWriter pooled = new(checked((int)totalSize));
        int n = snapshots.Count;
        using ArrayPoolList<ArenaReservation> reservationsList = new(n, n);
        Span<ArenaReservation> reservations = reservationsList.AsSpan();
        for (int i = 0; i < n; i++) reservations[i] = snapshots[i].Reservation;
        PersistedSnapshotMerger.NWayMergeSnapshots<PooledByteBufferWriter.Writer, ArenaReservation, ArenaByteReader, NoOpPin>(
            reservations, ref pooled.GetWriter(), bloom: Nethermind.State.Flat.Persistence.BloomFilter.BloomFilter.AlwaysTrue());
        return pooled.WrittenSpan.ToArray();
    }
}
