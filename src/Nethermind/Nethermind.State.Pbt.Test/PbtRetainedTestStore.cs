// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Buffers;
using Nethermind.State.Flat.Io;
using System.IO;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Pbt.PersistedSnapshots;

namespace Nethermind.State.Pbt.Test;

internal sealed class PbtRetainedTestStore : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pbt-retained-codec-" + Guid.NewGuid().ToString("N"));
    internal ArenaManager Arena { get; }
    internal BlobArenaManager Blobs { get; }
    internal TrackingMemoryProvider Memory { get; } = new();

    internal PbtRetainedTestStore()
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

    internal PbtRetainedSnapshot Build(PbtSnapshot snapshot)
    {
        using BlobArenaWriter blobs = Blobs.CreateWriter(PbtRetainedSnapshotBuilder.EstimateBlobSize(snapshot));
        using ArenaWriter writer = Arena.CreateWriter(PbtRetainedSnapshotBuilder.EstimateSize(snapshot));
        using RefCountedBloomFilter bloom = RefCountedBloomFilter.AlwaysTrue();
        PbtRetainedSnapshotBuilder.Build(snapshot, ref writer.GetWriter(), blobs, bloom.Filter);
        (SnapshotLocation location, ArenaReservation reservation) = writer.Complete();
        using (reservation)
        {
            blobs.Complete();
            blobs.Fsync();
            reservation.Fsync();
            return new(new(snapshot.From, snapshot.To, location, SnapshotTier.PersistedBase), reservation, Blobs, Memory, bloom);
        }
    }

    internal PbtRetainedSnapshot BuildRaw(PbtRetainedMetadata metadata, IReadOnlyList<(byte[] Key, byte[] Value)> records, Action<byte[]>? corrupt = null)
    {
        using ArenaWriter writer = Arena.CreateWriter(1024 * 1024);
        if (corrupt is null) PbtRetainedSnapshotBuilder.BuildRecords(records, ref writer.GetWriter());
        else
        {
            TestTableWriter buffer = new();
            PbtRetainedSnapshotBuilder.BuildRecords(records, ref buffer);
            byte[] bytes = buffer.Buffer.WrittenSpan.ToArray();
            corrupt(bytes);
            IByteBufferWriter.Copy(ref writer.GetWriter(), bytes);
        }
        (SnapshotLocation location, ArenaReservation reservation) = writer.Complete();
        using (reservation)
        {
            using RefCountedBloomFilter bloom = RefCountedBloomFilter.AlwaysTrue();
            return new(new(metadata.From, metadata.To, location, SnapshotTier.PersistedBase), reservation, Blobs, Memory, bloom);
        }
    }

    private sealed class TestTableWriter : IByteBufferWriter
    {
        internal ArrayBufferWriter<byte> Buffer { get; } = new();
        public Span<byte> GetSpan(int sizeHint) => Buffer.GetSpan(sizeHint);
        public void Advance(int count) => Buffer.Advance(count);
        public long Written => Buffer.WrittenCount;
        public long FirstOffset => 0;
    }

    public void Dispose()
    {
        Arena.Dispose();
        Blobs.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
