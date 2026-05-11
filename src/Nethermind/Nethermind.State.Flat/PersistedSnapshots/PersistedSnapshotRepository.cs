// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Collections;
using Nethermind.Db;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.Storage;
using Prometheus;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Per-tier persisted-snapshot store. The codebase wires two instances:
/// <list type="bullet">
///   <item>Small repo: accepts snapshots whose block range
///   <c>To - From &lt; CompactSize</c> (base in-memory snapshots persisted
///   directly). Its compactor merges short-range snapshots within
///   <c>&lt; CompactSize</c>; it never produces a CompactSize-sized result.</item>
///   <item>Large repo: accepts snapshots of size exactly <c>CompactSize</c>
///   (the in-memory compactor's output handed off via
///   <c>ConvertSnapshotToPersistedSnapshot(snap, isPersistable: true)</c>).
///   Its compactor merges these into 2×, 4×, ... CompactSize spans.</item>
/// </list>
/// Each instance owns its <c>(ArenaManager, BlobArenaManager, BlobArenaCatalog,
/// SnapshotCatalog)</c> set. Blob arena ids are unique within a repo, not
/// across repos; <c>PersistedSnapshot</c>s only ever resolve <c>NodeRef</c>s
/// through their own repo's blob manager.
/// </summary>
public sealed class PersistedSnapshotRepository(
    IArenaManager arenaManager,
    IBlobArenaManager blobArenaManager,
    BlobArenaCatalog blobArenaCatalog,
    IDb catalogDb,
    IFlatDbConfig config) : IPersistedSnapshotRepository
{
    private readonly IArenaManager _arena = arenaManager;
    private readonly IBlobArenaManager _blobs = blobArenaManager;
    private readonly BlobArenaCatalog _blobArenaCatalog = blobArenaCatalog;
    private readonly SnapshotCatalog _catalog = new(catalogDb);
    private readonly int _compactSize = config.CompactSize;
    private readonly bool _validatePersistedSnapshot = config.ValidatePersistedSnapshot;
    private readonly double _bloomBitsPerKey = config.PersistedSnapshotBloomBitsPerKey;
    private readonly double _trieBloomBitsPerKey = config.PersistedSnapshotTrieBloomBitsPerKey;
    private readonly ConcurrentDictionary<StateId, PersistedSnapshot> _baseSnapshots = new();
    private readonly ConcurrentDictionary<StateId, PersistedSnapshot> _compactedSnapshots = new();
    private readonly ConcurrentDictionary<StateId, PersistedSnapshot> _persistableCompactedSnapshots = new();
    private readonly PersistedSnapshotBloomFilterManager _bloomManager = new();
    private readonly Lock _catalogLock = new();
    private int _nextId;

    private bool BloomEnabled => _bloomBitsPerKey > 0 && _trieBloomBitsPerKey > 0;

    public PersistedSnapshotBloomFilterManager BloomManager => _bloomManager;

    public int SnapshotCount => _baseSnapshots.Count + _compactedSnapshots.Count + _persistableCompactedSnapshots.Count;
    public long BaseSnapshotMemory => SumMemory(_baseSnapshots);
    public long CompactedSnapshotMemory => SumMemory(_compactedSnapshots) + SumMemory(_persistableCompactedSnapshots);
    public int ArenaFileCount => _arena.ArenaFileCount;
    public long ArenaMappedBytes => _arena.ArenaMappedBytes;

    /// <summary>
    /// Load this tier's persisted snapshots from its catalog. Routes each
    /// loaded snapshot into the right in-memory dictionary based on its block
    /// range (the same band the repo is supposed to hold — entries outside
    /// the band are anomalous and would surface during routine reads).
    /// </summary>
    public void LoadFromCatalog()
    {
        lock (_catalogLock)
        {
            // Blob arena catalog first — rehydrates the BlobArenaManager so the
            // PersistedSnapshot ctor's TryAcquireBlobArena calls (driven by each
            // snapshot's ref_ids metadata) can resolve the ids.
            _blobArenaCatalog.Load();
            _blobs.Initialize(_blobArenaCatalog.Entries);

            _catalog.Load();
            List<SnapshotCatalog.CatalogEntry> entries = [.. _catalog.Entries];
            _arena.Initialize(entries);

            foreach (SnapshotCatalog.CatalogEntry entry in entries)
                LoadSnapshot(entry);

            _nextId = _catalog.NextId();
        }
    }

    private void LoadSnapshot(SnapshotCatalog.CatalogEntry entry)
    {
        long range = entry.To.BlockNumber - entry.From.BlockNumber;
        string tag = range < _compactSize
            ? ArenaReservationTags.BlobBackedSmall
            : ArenaReservationTags.BlobBackedLarge;
        ArenaReservation reservation = _arena.Open(entry.Location, tag);

        // Recover the snapshot's referenced blob arena ids from its on-disk metadata.
        int[]? refIds;
        using (WholeReadSession refIdsSession = reservation.BeginWholeReadSession())
        {
            WholeReadSessionReader refIdsReader = refIdsSession.GetReader();
            refIds = PersistedSnapshot.ReadRefIdsFromMetadata<WholeReadSessionReader, NoOpPin>(in refIdsReader);
        }

        Dictionary<int, BlobArenaFile> blobFiles = LeaseBlobFiles(refIds);
        PersistedSnapshot snapshot;
        try
        {
            snapshot = new(entry.Id, entry.From, entry.To, reservation, blobFiles);
        }
        catch
        {
            foreach (BlobArenaFile f in blobFiles.Values) f.Dispose();
            throw;
        }
        RegisterBlooms(snapshot);

        if (range < _compactSize)
            _baseSnapshots[entry.To] = snapshot;
        else if (range == _compactSize)
            _persistableCompactedSnapshots[entry.To] = snapshot;
        else
            _compactedSnapshots[entry.To] = snapshot;
    }

    /// <summary>
    /// Lease one <see cref="BlobArenaFile"/> per id in <paramref name="ids"/>. If any
    /// lease fails the helper releases what was acquired and throws — callers can
    /// trust the returned dict is fully leased or no leases are dangling.
    /// </summary>
    private Dictionary<int, BlobArenaFile> LeaseBlobFiles(IEnumerable<int>? ids)
    {
        Dictionary<int, BlobArenaFile> result = [];
        if (ids is null) return result;
        try
        {
            foreach (int id in ids)
            {
                if (!_blobs.TryLeaseFile(id, out BlobArenaFile? file))
                    throw new InvalidOperationException($"Blob arena {id} not registered in this tier");
                result[id] = file;
            }
            return result;
        }
        catch
        {
            foreach (BlobArenaFile f in result.Values) f.Dispose();
            throw;
        }
    }

    private readonly Histogram _persistedSnapshotSize = Prometheus.Metrics.CreateHistogram("persisted_snapshot_size", "persisted_snapshot_size", "type");

    /// <summary>
    /// Persist an in-memory snapshot to this tier. Caller is responsible for
    /// dispatching to the correct repo (small vs large) — this repo writes
    /// unconditionally to its own <see cref="_arena"/> + <see cref="_blobs"/>.
    /// <paramref name="isPersistable"/> selects the in-memory dict:
    /// <c>true</c> → <see cref="_persistableCompactedSnapshots"/>, <c>false</c>
    /// → <see cref="_baseSnapshots"/>.
    /// </summary>
    public void ConvertSnapshotToPersistedSnapshot(Snapshot snapshot, bool isPersistable = false)
    {
        BloomFilter? bloom = null;
        if (_bloomBitsPerKey > 0)
        {
            long capacity = (long)snapshot.AccountsCount
                          + snapshot.Content.SelfDestructedStorageAddresses.Count
                          + 2L * snapshot.StoragesCount;
            bloom = new BloomFilter(Math.Max(capacity, 1), _bloomBitsPerKey);
        }

        BloomFilter? trieBloom = null;
        if (_trieBloomBitsPerKey > 0)
        {
            long trieCapacity = (long)snapshot.StateNodesCount + snapshot.StorageNodesCount;
            trieBloom = new BloomFilter(Math.Max(trieCapacity, 1), _trieBloomBitsPerKey);
        }

        long estimatedSize = PersistedSnapshotBuilder.EstimateSize(snapshot);
        string metaTag = isPersistable ? ArenaReservationTags.BlobBackedLarge : ArenaReservationTags.BlobBackedSmall;
        string blobTag = isPersistable ? ArenaReservationTags.BlobLarge : ArenaReservationTags.BlobSmall;

        SnapshotLocation location;
        ArenaReservation reservation;
        int blobArenaId;
        using BlobArenaWriter blobWriter = _blobs.CreateWriter(estimatedSize, blobTag);
        using (ArenaWriter arenaWriter = _arena.CreateWriter(estimatedSize, metaTag))
        {
            PersistedSnapshotBuilder.Build<ArenaBufferWriter, ArenaBufferReader, NoOpPin>(
                snapshot, ref arenaWriter.GetWriter(), blobWriter, bloom, trieBloom);
            _persistedSnapshotSize.WithLabels(isPersistable ? "is_persistable" : "base").Observe(arenaWriter.GetWriter().Written);
            (location, reservation) = arenaWriter.Complete();
        }
        blobWriter.Complete();
        blobArenaId = blobWriter.BlobArenaId;

        Dictionary<int, BlobArenaFile> blobFiles = LeaseBlobFiles([blobArenaId]);
        lock (_catalogLock)
        {
            int id = _nextId++;
            _catalog.Add(new SnapshotCatalog.CatalogEntry(id, snapshot.From, snapshot.To, location));
            _catalog.Save();

            PersistedSnapshot persisted;
            try
            {
                persisted = new(id, snapshot.From, snapshot.To, reservation, blobFiles);
            }
            catch
            {
                foreach (BlobArenaFile f in blobFiles.Values) f.Dispose();
                throw;
            }
            RegisterBlooms(persisted, bloom, trieBloom);
            if (_validatePersistedSnapshot)
                PersistedSnapshotUtils.ValidatePersistedSnapshot(snapshot, persisted, _bloomManager);
            if (isPersistable)
                _persistableCompactedSnapshots[snapshot.To] = persisted;
            else
                _baseSnapshots[snapshot.To] = persisted;
        }

        // Drop freshly-written pages from the kernel page cache — not on the
        // read working set yet.
        reservation.AdviseDontNeed();

        // Release the writers' "creation" leases. PersistedSnapshot took its own
        // (metadata reservation + the blob arena lease via BlobArenaFile) in the ctor.
        reservation.Dispose();
        _blobs.ReleaseBlobArena(blobArenaId);
    }

    /// <summary>
    /// Store a compacted snapshot with a pre-computed location and reservation.
    /// <paramref name="referencedBlobArenaIds"/> is the union of blob arena ids
    /// inherited from the inputs of the N-way merge that produced this snapshot.
    /// </summary>
    public void AddCompactedSnapshot(StateId from, StateId to, SnapshotLocation location, ArenaReservation reservation, HashSet<int> referencedBlobArenaIds, bool isPersistable, BloomFilter? bloom = null)
    {
        Dictionary<int, BlobArenaFile> blobFiles = LeaseBlobFiles(referencedBlobArenaIds);
        lock (_catalogLock)
        {
            int id = _nextId++;
            _catalog.Add(new SnapshotCatalog.CatalogEntry(id, from, to, location));
            _catalog.Save();

            PersistedSnapshot snapshot;
            try
            {
                snapshot = new(id, from, to, reservation, blobFiles);
            }
            catch
            {
                foreach (BlobArenaFile f in blobFiles.Values) f.Dispose();
                throw;
            }
            RegisterBlooms(snapshot, bloom, trieBloom: null);
            if (isPersistable)
                _persistableCompactedSnapshots[to] = snapshot;
            else
                _compactedSnapshots[to] = snapshot;
        }

        // Release the caller's "creation" lease — see ConvertSnapshotToPersistedSnapshot.
        reservation.Dispose();
    }

    /// <summary>
    /// Assemble persisted snapshots for compaction, walking backward from toStateId.
    /// If a compacted snapshot spans too far back (below minBlockNumber), fall back to base.
    /// Returns oldest-first list, or empty if fewer than 2 snapshots found.
    /// Mirrors <see cref="SnapshotRepository.AssembleSnapshotsUntil"/>.
    /// </summary>
    public PersistedSnapshotList AssembleSnapshotsForCompaction(StateId toStateId, long minBlockNumber)
    {
        PersistedSnapshotList result = new(0);
        StateId current = toStateId;

        while (true)
        {
            PersistedSnapshot? snapshot;

            // Try compacted first
            if (_compactedSnapshots.TryGetValue(current, out PersistedSnapshot? compacted))
            {
                if (compacted.From.BlockNumber < minBlockNumber)
                {
                    // Compacted spans too far back, try base
                    if (_baseSnapshots.TryGetValue(current, out PersistedSnapshot? baseSnap))
                    {
                        if (baseSnap.From.BlockNumber < minBlockNumber)
                            break; // Base also spans too far
                        snapshot = baseSnap;
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    snapshot = compacted;
                }
            }
            else if (_baseSnapshots.TryGetValue(current, out PersistedSnapshot? baseSnap))
            {
                if (baseSnap.From.BlockNumber < minBlockNumber)
                    break;
                snapshot = baseSnap;
            }
            else
            {
                break;
            }

            if (!snapshot.TryAcquire())
            {
                result.Dispose();
                return PersistedSnapshotList.Empty();
            }

            result.Add(snapshot);

            if (snapshot.From == current)
                break; // Prevent infinite loop

            if (snapshot.From.BlockNumber == minBlockNumber)
                break;

            current = snapshot.From;
        }

        if (result.Count < 2)
        {
            result.Dispose();
            return PersistedSnapshotList.Empty();
        }

        result.Reverse(); // oldest-first
        return result;
    }

    public bool TryLeaseSnapshotTo(StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot)
    {
        if (_baseSnapshots.TryGetValue(toState, out snapshot) && snapshot.TryAcquire())
            return true;
        snapshot = null;
        return false;
    }

    public bool TryLeaseCompactedSnapshotTo(StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot)
    {
        if (_compactedSnapshots.TryGetValue(toState, out snapshot) && snapshot.TryAcquire())
            return true;
        if (_persistableCompactedSnapshots.TryGetValue(toState, out snapshot) && snapshot.TryAcquire())
            return true;
        snapshot = null;
        return false;
    }

    public bool TryLeasePersistableCompactedSnapshotTo(StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot)
    {
        if (_persistableCompactedSnapshots.TryGetValue(toState, out snapshot) && snapshot.TryAcquire())
            return true;
        snapshot = null;
        return false;
    }

    /// <summary>
    /// Find the snapshot whose From matches the given state. Tries compacted first (larger range = faster catch-up), then base.
    /// </summary>
    public PersistedSnapshot? TryGetSnapshotFrom(StateId fromState)
    {
        foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _compactedSnapshots)
        {
            PersistedSnapshot snapshot = kv.Value;
            if (snapshot.From == fromState && snapshot.TryAcquire())
                return snapshot;
        }

        foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _baseSnapshots)
        {
            PersistedSnapshot snapshot = kv.Value;
            if (snapshot.From == fromState && snapshot.TryAcquire())
                return snapshot;
        }

        return null;
    }

    /// <summary>
    /// Prune snapshots with To.BlockNumber before the given state. Blob arenas referenced
    /// by surviving compacted snapshots stay alive automatically via the
    /// <see cref="IBlobArenaManager"/> refcount — no explicit "referenced base id"
    /// check is needed at this layer.
    /// </summary>
    public int PruneBefore(StateId stateId)
    {
        lock (_catalogLock)
        {
            int pruned = 0;

            using ArrayPoolList<StateId> baseToRemove = new(0);
            foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _baseSnapshots)
            {
                if (kv.Value.To.BlockNumber < stateId.BlockNumber)
                    baseToRemove.Add(kv.Key);
            }
            foreach (StateId key in baseToRemove)
            {
                if (_baseSnapshots.TryRemove(key, out PersistedSnapshot? snapshot))
                {
                    RemoveFromCatalog(snapshot.Id);
                    snapshot.Dispose();
                    pruned++;
                }
            }

            // Prune compacted snapshots
            using ArrayPoolList<StateId> compactedToRemove = new(0);
            foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _compactedSnapshots)
            {
                if (kv.Value.To.BlockNumber < stateId.BlockNumber)
                    compactedToRemove.Add(kv.Key);
            }
            foreach (StateId key in compactedToRemove)
            {
                if (_compactedSnapshots.TryRemove(key, out PersistedSnapshot? snapshot))
                {
                    RemoveFromCatalog(snapshot.Id);
                    snapshot.Dispose();
                    pruned++;
                }
            }

            // Prune persistable compacted snapshots
            using ArrayPoolList<StateId> persistableToRemove = new(0);
            foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _persistableCompactedSnapshots)
            {
                if (kv.Value.To.BlockNumber < stateId.BlockNumber)
                    persistableToRemove.Add(kv.Key);
            }
            foreach (StateId key in persistableToRemove)
            {
                if (_persistableCompactedSnapshots.TryRemove(key, out PersistedSnapshot? snapshot))
                {
                    RemoveFromCatalog(snapshot.Id);
                    snapshot.Dispose();
                    pruned++;
                }
            }

            _bloomManager.PruneBefore(stateId);

            if (pruned > 0) _catalog.Save();
            return pruned;
        }
    }

    public bool HasBaseSnapshot(in StateId stateId) => _baseSnapshots.ContainsKey(stateId);

    /// <summary>
    /// Build any missing blooms (key/trie) for <paramref name="snapshot"/> and register
    /// the resulting <see cref="PersistedSnapshotBloom"/> wrapper with the bloom manager.
    /// Pre-built blooms (e.g. populated inline by the writer or compactor) can be passed
    /// in via <paramref name="keyBloom"/> / <paramref name="trieBloom"/>; nulls are
    /// rebuilt from the on-disk image via <see cref="PersistedSnapshotBloomBuilder"/>.
    /// No-op when the bloom feature is disabled in config.
    /// </summary>
    private void RegisterBlooms(PersistedSnapshot snapshot, BloomFilter? keyBloom = null, BloomFilter? trieBloom = null)
    {
        if (!BloomEnabled)
        {
            keyBloom?.Dispose();
            trieBloom?.Dispose();
            return;
        }

        keyBloom ??= PersistedSnapshotBloomBuilder.Build(snapshot, _bloomBitsPerKey);
        trieBloom ??= PersistedSnapshotBloomBuilder.BuildTrieBloom(snapshot, _trieBloomBitsPerKey);
        _bloomManager.Register(new PersistedSnapshotBloom(snapshot.From, snapshot.To, keyBloom, trieBloom));
    }

    private void RemoveFromCatalog(int snapshotId)
    {
        SnapshotCatalog.CatalogEntry? entry = _catalog.Find(snapshotId);
        if (entry is not null)
            _catalog.Remove(snapshotId);
    }

    private static long SumMemory(ConcurrentDictionary<StateId, PersistedSnapshot> dict)
    {
        long total = 0;
        foreach (KeyValuePair<StateId, PersistedSnapshot> kv in dict)
            total += kv.Value.Size;
        return total;
    }

    public void Dispose()
    {
        lock (_catalogLock)
        {
            // Dispose arena managers first so their _disposed flag is set before any
            // snapshot dispose runs MarkDead — otherwise a clean shutdown would treat
            // every still-leased snapshot as fully dead and delete the on-disk arena
            // files, wiping the catalog's data before the next session can reload it.
            _arena.Dispose();
            _blobs.Dispose();
            foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _baseSnapshots)
                kv.Value.Dispose();
            foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _compactedSnapshots)
                kv.Value.Dispose();
            foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _persistableCompactedSnapshots)
                kv.Value.Dispose();
            _baseSnapshots.Clear();
            _compactedSnapshots.Clear();
            _persistableCompactedSnapshots.Clear();
            _bloomManager.Dispose();
        }
    }
}
