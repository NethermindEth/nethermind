// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Attributes;
using Nethermind.Db;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat.PersistedSnapshots;

public interface IPersistedSnapshotConverter
{
    /// <summary>
    /// Persist an in-memory snapshot as a base entry in the persisted tier. The returned snapshot is
    /// pre-leased — the caller owns the lease and MUST dispose it.
    /// </summary>
    PersistedSnapshot Convert(Snapshot snapshot);
}

/// <summary>
/// Persists an in-memory <see cref="Snapshot"/> as a base entry in the persisted tier: builds its
/// HSST metadata + contiguous trie-RLP region into the shared arena/blob pools, fsyncs for
/// durability, then stores it in the repository's base bucket.
/// </summary>
/// <remarks>
/// Holds the same shared <see cref="IArenaManager"/> / <see cref="BlobArenaManager"/> instances the
/// <see cref="ISnapshotRepository"/> reads through — writing through different mmaps would corrupt
/// reads. The build half lives here (a persistence policy); the repository keeps only the
/// <see cref="ISnapshotRepository.AddPersistedSnapshot"/> store primitive.
/// </remarks>
public class PersistedSnapshotConverter(
    IArenaManager arena,
    BlobArenaManager blobs,
    IFlatDbConfig config,
    ISnapshotRepository repo) : IPersistedSnapshotConverter
{
    private readonly double _bloomBitsPerKey = config.PersistedSnapshotBloomBitsPerKey;
    private readonly bool _validatePersistedSnapshot = config.ValidatePersistedSnapshot;
    private static readonly StringLabel _tierLabel = new("persisted");

    private bool BloomEnabled => _bloomBitsPerKey > 0;

    /// <inheritdoc/>
    public PersistedSnapshot Convert(Snapshot snapshot)
    {
        // One unified bloom covering account/slot/SD keys + state-trie + storage-trie paths.
        // Sized as the union of both expected key counts at the configured bits-per-key.
        BloomFilter bloom;
        if (BloomEnabled)
        {
            long capacity = (long)snapshot.AccountsCount
                          + snapshot.Content.SelfDestructedStorageAddresses.Count
                          + 2L * snapshot.StoragesCount
                          + snapshot.StateNodesCount
                          + snapshot.StorageNodesCount;
            bloom = new BloomFilter(Math.Max(capacity, 1), _bloomBitsPerKey);
        }
        else
        {
            bloom = BloomFilter.AlwaysTrue();
        }

        long estimatedSize = PersistedSnapshotBuilder.EstimateSize(snapshot);

        SnapshotLocation location;
        ArenaReservation reservation;
        using BlobArenaWriter blobWriter = blobs.CreateWriter(estimatedSize);
        using (ArenaWriter arenaWriter = arena.CreateWriter(estimatedSize))
        {
            PersistedSnapshotBuilder.Build<ArenaBufferWriter>(
                snapshot, ref arenaWriter.GetWriter(), blobWriter, bloom);
            Metrics.PersistedSnapshotSize.Observe(arenaWriter.GetWriter().Written, _tierLabel);
            (location, reservation) = arenaWriter.Complete();
        }
        blobWriter.Complete();

        // Durability barrier — fsync both the metadata arena and the blob arena before the
        // catalog records the new entry. A crash between this point and the next persistence
        // checkpoint would otherwise leave the catalog pointing at unsynced pages whose
        // contents are not yet guaranteed to be on disk.
        reservation.Fsync();
        blobWriter.Fsync();

        // Store records the catalog entry into the base bucket, indexes the snapshot, and
        // pre-acquires the caller's lease under the bucket's lock; it also disposes the reservation.
        PersistedSnapshot persisted = repo.AddPersistedSnapshot(
            snapshot.From, snapshot.To, location, reservation, bloom, SnapshotTier.PersistedBase);

        if (_validatePersistedSnapshot)
            PersistedSnapshotUtils.ValidatePersistedSnapshot(snapshot, persisted);

        return persisted;
    }
}
