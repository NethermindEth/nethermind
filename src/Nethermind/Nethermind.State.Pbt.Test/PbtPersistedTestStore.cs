// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Pbt.PersistedSnapshots;
using Nethermind.State.Pbt.Snapshot;

namespace Nethermind.State.Pbt.Test;

internal sealed class PbtPersistedTestStore : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pbt-persisted-codec-" + Guid.NewGuid().ToString("N"));
    internal ArenaManager Arena { get; }
    internal BlobArenaManager Blobs { get; }
    internal TrackingMemoryProvider Memory { get; } = new();

    internal PbtPersistedTestStore()
    {
        Arena = new(Path.Combine(_directory, "arena"), new FlatDbConfig
        {
            ArenaFileSizeBytes = 1024 * 1024,
            PersistedSnapshotDedicatedArenaThresholdBytes = 1024 * 1024,
            PersistedSnapshotArenaPageCacheBytes = 0,
            PersistedSnapshotPunchHoleOnReclaim = false,
        }, LimboLogs.Instance);
        Blobs = new(Path.Combine(_directory, "blob"), 4 * 1024 * 1024);
    }

    internal PbtPersistedSnapshot Build(PbtSnapshot snapshot)
    {
        using BlobArenaWriter blobs = Blobs.CreateWriter(PbtPersistedSnapshotBuilder.EstimateBlobSize(snapshot));
        using ArenaWriter writer = Arena.CreateWriter(PbtPersistedSnapshotBuilder.EstimateSize(snapshot));
        using RefCountedBloomFilter bloom = RefCountedBloomFilter.AlwaysTrue();
        PbtPersistedSnapshotBuilder.Build(snapshot, ref writer.GetWriter(), blobs, bloom.Filter);
        (SnapshotLocation location, ArenaReservation reservation) = writer.Complete();
        using (reservation)
        {
            blobs.Complete();
            blobs.Fsync();
            reservation.Fsync();
            return new(new(snapshot.From, snapshot.To, location, SnapshotTier.PersistedBase), reservation, Blobs, Memory, bloom);
        }
    }

    public void Dispose()
    {
        Arena.Dispose();
        Blobs.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
