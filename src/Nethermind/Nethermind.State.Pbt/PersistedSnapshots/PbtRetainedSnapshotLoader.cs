// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using System.Runtime.ExceptionServices;
using Nethermind.Core.Memory;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Pbt.PersistedSnapshots;

internal sealed class PbtRetainedSnapshotLoader(
    PbtSnapshotRepository repository,
    PbtRetainedStorageLifetime lifetime,
    [KeyFilter(DbNames.Pbt)] IArenaManager arena,
    [KeyFilter(DbNames.Pbt)] BlobArenaManager blobs,
    [KeyFilter(DbNames.Pbt)] ISnapshotCatalog catalog,
    IRefCountingMemoryProvider memory,
    IPbtConfig config,
    ILogManager logManager) : IPbtRetainedSnapshotLoader
{
    private readonly ILogger _logger = logManager.GetClassLogger<PbtRetainedSnapshotLoader>();
    private readonly Lock _loadLock = new();
    private bool _loaded;
    private ExceptionDispatchInfo? _loadFailure;
    private int _disposed;

    public void Load()
    {
        lock (_loadLock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _loadFailure?.Throw();
            if (_loaded) return;
            try { LoadCore(); _loaded = true; }
            catch (Exception error) { _loadFailure = ExceptionDispatchInfo.Capture(error); throw; }
        }
    }

    private void LoadCore()
    {
        List<ArenaReservation> reservations = [];
        try
        {
            blobs.Initialize();
            List<CatalogEntry> entries = [.. catalog.Load()];
            foreach (CatalogEntry entry in entries)
                if (entry.Location.ArenaId < 0 || entry.Location.Offset < 0 || entry.Location.Size <= 0
                    || entry.Location.Size > long.MaxValue - entry.Location.Offset)
                    throw new InvalidDataException("Invalid retained PBT catalog location.");
            IReadOnlySet<int> missing = arena.Initialize(entries);
            foreach (CatalogEntry entry in entries)
            {
                if (missing.Contains(entry.Location.ArenaId))
                {
                    if (_logger.IsWarn) _logger.Warn($"Retained PBT state {entry.To} has no arena {entry.Location.ArenaId}; removing its stale catalog entry.");
                    catalog.Remove(entry.To, Depth(entry.From, entry.To));
                    continue;
                }
                if (arena is ArenaManager mapped && !mapped.ContainsLocation(entry.Location))
                    throw new InvalidDataException("Retained PBT catalog location exceeds its arena file.");
                ArenaReservation reservation = arena.Open(entry.Location);
                reservations.Add(reservation);
                using RefCountedBloomFilter bloom = RefCountedBloomFilter.AlwaysTrue();
                using PbtRetainedSnapshot snapshot = new(entry, reservation, blobs, memory, bloom);
                lock (repository.PublicationGate.Sync)
                {
                    if (!repository.TryAddRetained(snapshot))
                        throw new InvalidDataException($"Duplicate retained PBT catalog identity {entry.To}.");
                }
            }
            ReconstructBloom(entries);
            blobs.SweepUnreferenced();
        }
        catch
        {
            lifetime.AbortLoad();
            foreach (ArenaReservation reservation in reservations) reservation.PersistOnShutdown();
            repository.MarkPersistedTierForShutdown();
            throw;
        }
        finally
        {
            foreach (ArenaReservation reservation in reservations) reservation.Dispose();
        }
    }

    public bool ConvertAndRegister(PbtSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using BlobArenaWriter blobWriter = blobs.CreateWriter(PbtRetainedSnapshotBuilder.EstimateBlobSize(snapshot));
        using RefCountedBloomFilter bloom = new(config.PersistedSnapshotBloomBitsPerKey > 0
            ? new BloomFilter(1, config.PersistedSnapshotBloomBitsPerKey) : BloomFilter.AlwaysTrue());
        SnapshotLocation location;
        ArenaReservation reservation;
        using (ArenaWriter writer = arena.CreateWriter(PbtRetainedSnapshotBuilder.EstimateSize(snapshot), small: true))
        {
            PbtRetainedSnapshotBuilder.Build(snapshot, ref writer.GetWriter(), blobWriter, bloom.Filter);
            (location, reservation) = writer.Complete();
        }
        using (reservation)
        {
            blobWriter.Complete();
            reservation.Fsync();
            blobWriter.Fsync();
            using PbtRetainedSnapshot retained = new(new(snapshot.From, snapshot.To, location, SnapshotTier.PersistedBase), reservation, blobs, memory, bloom);
            if (config.ValidatePersistedSnapshot) PbtRetainedSnapshotValidation.Validate(snapshot, retained);
            lock (repository.PublicationGate.Sync)
            {
                if (!repository.ContainsMemorySource(snapshot)) return false;
                return Publish(retained, duplicateSuccess: true);
            }
        }
    }

    public bool RegisterCompacted(PbtRetainedSnapshot snapshot, ReadOnlySpan<PbtRetainedSnapshot> sources)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (sources.Length < 2) return false;
        lock (repository.PublicationGate.Sync)
        {
            if (snapshot.From != sources[0].From || snapshot.To != sources[^1].To || snapshot.TreeRoot != sources[^1].TreeRoot)
                throw new InvalidDataException("Retained PBT compaction output identity mismatch.");
            for (int i = 0; i < sources.Length; i++)
            {
                if (!repository.ContainsRetainedStorageSource(sources[i])) return false;
                if (i > 0 && sources[i - 1].To != sources[i].From)
                    throw new InvalidDataException("Disconnected retained PBT compaction sources.");
            }
            snapshot.Reservation.Fsync();
            return Publish(snapshot, duplicateSuccess: false);
        }
    }

    private bool Publish(PbtRetainedSnapshot snapshot, bool duplicateSuccess)
    {
        long depth = Depth(snapshot.From, snapshot.To);
        if (repository.TryLeaseRetainedCatalogKey(snapshot.To, depth, out PbtRetainedSnapshot? existing))
        {
            using (existing)
            {
                if (existing!.From != snapshot.From || existing.TreeRoot != snapshot.TreeRoot)
                    throw new InvalidDataException("Conflicting retained PBT catalog identity.");
                return duplicateSuccess;
            }
        }
        try
        {
            catalog.Add(new(snapshot.From, snapshot.To, snapshot.Location, snapshot.Tier));
            if (!repository.TryAddRetained(snapshot)) throw new InvalidOperationException("Retained PBT admission lost under publication lock.");
            return true;
        }
        catch (Exception publicationError)
        {
            try
            {
                catalog.Remove(snapshot.To, depth);
            }
            catch (Exception rollbackError)
            {
                snapshot.PersistOnShutdown();
                if (_logger.IsError) _logger.Error("Retained PBT publication rollback failed; preserving potentially catalogued data for recovery.", rollbackError);
                throw new AggregateException(publicationError, rollbackError);
            }
            throw;
        }
    }

    private void ReconstructBloom(List<CatalogEntry> entries)
    {
        if (config.PersistedSnapshotBloomBitsPerKey <= 0 || entries.Count == 0
            || repository.GetLastSnapshotId() is not StateId head) return;
        StateId floor = entries[0].From;
        foreach (CatalogEntry entry in entries)
            if ((long)entry.From.BlockNumber < (long)floor.BlockNumber) floor = entry.From;
        if (head == floor) return;
        using PbtSnapshotChain? chain = repository.TryLeaseReadChain(head, floor);
        if (chain is null) return;
        foreach (PbtSnapshotLease layer in chain.Layers)
        {
            PbtRetainedSnapshot snapshot = layer.Retained!;
            using RefCountedBloomFilter rebuilt = RebuildBloom(snapshot);
            using PbtRetainedSnapshot replacement = snapshot.WithBloom(rebuilt);
            if (repository.ReplaceRetainedSnapshot(snapshot, replacement))
                repository.ShareBloomAcrossRange(snapshot.From, snapshot.To, rebuilt);
        }
    }

    private RefCountedBloomFilter RebuildBloom(PbtRetainedSnapshot snapshot)
    {
        if (config.PersistedSnapshotBloomBitsPerKey <= 0) return RefCountedBloomFilter.AlwaysTrue();
        long count = 0;
        using (PbtRetainedScanner scanner = snapshot.Scan())
            while (scanner.MoveNext())
                if (IsEntity(scanner.Key)) count++;
        RefCountedBloomFilter result = new(new BloomFilter(Math.Max(1, count), config.PersistedSnapshotBloomBitsPerKey));
        try
        {
            using PbtRetainedScanner scanner = snapshot.Scan();
            while (scanner.MoveNext())
                if (IsEntity(scanner.Key)) result.Filter.AddUnsynchronized(PbtRetainedKey.BloomHash(scanner.Key));
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static bool IsEntity(ReadOnlySpan<byte> key) => key[0] != 0 && key[0] != PbtRetainedKey.Ownership && !PbtRetainedSnapshot.IsChunk(key);

    private static long Depth(in StateId from, in StateId to) => unchecked((long)(to.BlockNumber - from.BlockNumber));

    public void Dispose()
    {
        lock (_loadLock)
            if (Interlocked.Exchange(ref _disposed, 1) == 0) repository.MarkPersistedTierForShutdown();
    }
}
