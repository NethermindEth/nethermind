// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Collections.Pooled;
using Nethermind.Core.Collections;
using Nethermind.Db;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.Storage;
using Prometheus;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Manages persisted snapshots on disk with a two-layer design (base + compacted),
/// mirroring <see cref="SnapshotRepository"/>'s pattern.
/// </summary>
public sealed class PersistedSnapshotRepository(
    IArenaManager smallArenaManager,
    IBlobArenaManager smallBlobArenaManager,
    IArenaManager largeArenaManager,
    IBlobArenaManager largeBlobArenaManager,
    BlobArenaCatalog blobArenaCatalog,
    IDb catalogDb,
    IFlatDbConfig config) : IPersistedSnapshotRepository
{
    private readonly IArenaManager _smallArenaManager = smallArenaManager;
    private readonly IBlobArenaManager _smallBlobArenaManager = smallBlobArenaManager;
    private readonly IArenaManager _largeArenaManager = largeArenaManager;
    private readonly IBlobArenaManager _largeBlobArenaManager = largeBlobArenaManager;
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
    public int ArenaFileCount => _smallArenaManager.ArenaFileCount + _largeArenaManager.ArenaFileCount;
    public long ArenaMappedBytes => _smallArenaManager.ArenaMappedBytes + _largeArenaManager.ArenaMappedBytes;

    /// <summary>
    /// Load all persisted snapshots from catalog and arena files. Tier (small / large)
    /// is determined by block range against <c>CompactSize</c>; the legacy
    /// <c>PersistedSnapshotType</c> distinction is gone.
    /// </summary>
    public void LoadFromCatalog()
    {
        lock (_catalogLock)
        {
            // Blob arena catalog first — rehydrates each BlobArenaManager so the
            // PersistedSnapshot ctor's TryAcquireBlobArena calls (driven by each
            // snapshot's ref_ids metadata) can resolve the ids.
            _blobArenaCatalog.Load();
            _smallBlobArenaManager.Initialize(_blobArenaCatalog.Entries);
            _largeBlobArenaManager.Initialize(_blobArenaCatalog.Entries);

            _catalog.Load();
            List<SnapshotCatalog.CatalogEntry> smallEntries = [];
            List<SnapshotCatalog.CatalogEntry> largeEntries = [];
            foreach (SnapshotCatalog.CatalogEntry entry in _catalog.Entries)
            {
                if (IsSmallRange(entry)) smallEntries.Add(entry);
                else largeEntries.Add(entry);
            }
            _smallArenaManager.Initialize(smallEntries);
            _largeArenaManager.Initialize(largeEntries);

            foreach (SnapshotCatalog.CatalogEntry entry in _catalog.Entries)
                LoadSnapshot(entry);

            _nextId = _catalog.NextId();
        }
    }

    private void LoadSnapshot(SnapshotCatalog.CatalogEntry entry)
    {
        bool isSmall = IsSmallRange(entry);
        string tag = isSmall
            ? ArenaReservationTags.BlobBackedSmall
            : ArenaReservationTags.BlobBackedLarge;
        IArenaManager arenaMgr = isSmall ? _smallArenaManager : _largeArenaManager;
        IBlobArenaManager blobMgr = isSmall ? _smallBlobArenaManager : _largeBlobArenaManager;
        ArenaReservation reservation = arenaMgr.Open(entry.Location, tag);

        // Recover the snapshot's referenced blob arena ids from its on-disk metadata.
        int[]? refIds;
        using (WholeReadSession refIdsSession = reservation.BeginWholeReadSession())
        {
            WholeReadSessionReader refIdsReader = refIdsSession.GetReader();
            refIds = PersistedSnapshot.ReadRefIdsFromMetadata<WholeReadSessionReader, NoOpPin>(in refIdsReader);
        }

        PersistedSnapshot snapshot = new(entry.Id, entry.From, entry.To, reservation, _smallBlobArenaManager, _largeBlobArenaManager, refIds);
        RegisterBlooms(snapshot);

        long range = entry.To.BlockNumber - entry.From.BlockNumber;
        if (range < _compactSize)
            _baseSnapshots[entry.To] = snapshot;
        else if (range == _compactSize)
            _persistableCompactedSnapshots[entry.To] = snapshot;
        else
            _compactedSnapshots[entry.To] = snapshot;
    }

    private readonly Histogram _persistedSnapshotSize = Prometheus.Metrics.CreateHistogram("persisted_snapshot_size", "persisted_snapshot_size", "type");

    /// <summary>
    /// Persist an in-memory snapshot to disk. Metadata HSST goes to the tier's
    /// <see cref="IArenaManager"/> (small if <c>To-From &lt; CompactSize</c>, large
    /// otherwise); trie-node RLPs are appended to a fresh <see cref="BlobArenaWriter"/>
    /// against the tier's <see cref="IBlobArenaManager"/>. The blob arena id is
    /// recorded in the snapshot's metadata column under <c>ref_ids</c>.
    /// </summary>
    public void ConvertSnapshotToPersistedSnapshot(Snapshot snapshot, bool isPersistable = false)
    {
        bool isSmall = (snapshot.To.BlockNumber - snapshot.From.BlockNumber) < _compactSize;
        IArenaManager arena = isSmall ? _smallArenaManager : _largeArenaManager;
        IBlobArenaManager blobMgr = isSmall ? _smallBlobArenaManager : _largeBlobArenaManager;

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
        string metaTag = isSmall ? ArenaReservationTags.BlobBackedSmall : ArenaReservationTags.BlobBackedLarge;
        string blobTag = isSmall ? ArenaReservationTags.BlobSmall : ArenaReservationTags.BlobLarge;

        SnapshotLocation location;
        ArenaReservation reservation;
        int blobArenaId;
        using BlobArenaWriter blobWriter = blobMgr.CreateWriter(estimatedSize, blobTag);
        using (ArenaWriter arenaWriter = arena.CreateWriter(estimatedSize, metaTag))
        {
            PersistedSnapshotBuilder.Build<ArenaBufferWriter, ArenaBufferReader, NoOpPin>(
                snapshot, ref arenaWriter.GetWriter(), blobWriter, bloom, trieBloom);
            _persistedSnapshotSize.WithLabels(isPersistable ? "is_persistable" : "base").Observe(arenaWriter.GetWriter().Written);
            (location, reservation) = arenaWriter.Complete();
        }
        blobWriter.Complete();
        blobArenaId = blobWriter.BlobArenaId;

        lock (_catalogLock)
        {
            int id = _nextId++;
            _catalog.Add(new SnapshotCatalog.CatalogEntry(id, snapshot.From, snapshot.To, location));
            _catalog.Save();

            int[] referencedBlobArenaIds = [blobArenaId];
            PersistedSnapshot persisted = new(id, snapshot.From, snapshot.To, reservation, _smallBlobArenaManager, _largeBlobArenaManager, referencedBlobArenaIds);
            RegisterBlooms(persisted, bloom, trieBloom);
            if (_validatePersistedSnapshot)
                PersistedSnapshotUtils.ValidatePersistedSnapshot(snapshot, persisted, _bloomManager);
            if (isPersistable)
                _persistableCompactedSnapshots[snapshot.To] = persisted;
            else
                _baseSnapshots[snapshot.To] = persisted;
        }

        // Drop freshly-written pages from the kernel page cache for both reservations —
        // neither is on the read working set yet.
        reservation.AdviseDontNeed();

        // Release the writers' "creation" leases. PersistedSnapshot took its own
        // (metadata reservation + each blob arena id) via AcquireLease in the ctor.
        reservation.Dispose();
        blobMgr.ReleaseBlobArena(blobArenaId);
    }

    /// <summary>
    /// Store a compacted snapshot with a pre-computed location and reservation.
    /// <paramref name="referencedBlobArenaIds"/> is the union of blob arena ids
    /// inherited from the inputs of the N-way merge that produced this snapshot.
    /// </summary>
    public void AddCompactedSnapshot(StateId from, StateId to, SnapshotLocation location, ArenaReservation reservation, HashSet<int> referencedBlobArenaIds, bool isPersistable, BloomFilter? bloom = null)
    {
        lock (_catalogLock)
        {
            int id = _nextId++;
            _catalog.Add(new SnapshotCatalog.CatalogEntry(id, from, to, location));
            _catalog.Save();

            int[] refIds = [.. referencedBlobArenaIds];
            PersistedSnapshot snapshot = new(id, from, to, reservation, _smallBlobArenaManager, _largeBlobArenaManager, refIds);
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

    private bool IsPersistableSize(SnapshotCatalog.CatalogEntry entry) =>
        entry.To.BlockNumber - entry.From.BlockNumber == _compactSize;

    private bool IsSmallRange(SnapshotCatalog.CatalogEntry entry) =>
        entry.To.BlockNumber - entry.From.BlockNumber < _compactSize;

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
            _smallArenaManager.Dispose();
            _largeArenaManager.Dispose();
            _smallBlobArenaManager.Dispose();
            _largeBlobArenaManager.Dispose();
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
