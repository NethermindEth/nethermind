// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Db;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Storage;
using Prometheus;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Manages persisted snapshots on disk with a two-layer design (base + compacted),
/// mirroring <see cref="SnapshotRepository"/>'s pattern.
/// </summary>
public sealed class PersistedSnapshotRepository : IPersistedSnapshotRepository
{
    private readonly IArenaManager _arenaManager;
    private readonly SnapshotCatalog _catalog;
    private readonly int _compactSize;
    private readonly ConcurrentDictionary<StateId, PersistedSnapshot> _baseSnapshots = new();
    private readonly ConcurrentDictionary<StateId, PersistedSnapshot> _compactedSnapshots = new();
    private readonly ConcurrentDictionary<StateId, PersistedSnapshot> _persistableCompactedSnapshots = new();
    private readonly object _catalogLock = new();
    private int _nextId;

    public PersistedSnapshotRepository(IArenaManager arenaManager, string basePath, IFlatDbConfig config)
    {
        _arenaManager = arenaManager;
        _catalog = new SnapshotCatalog(Path.Combine(basePath, "catalog.bin"));
        _compactSize = config.CompactSize;
    }

    public int SnapshotCount => _baseSnapshots.Count + _compactedSnapshots.Count + _persistableCompactedSnapshots.Count;
    public long BaseSnapshotMemory => SumMemory(_baseSnapshots);
    public long CompactedSnapshotMemory => SumMemory(_compactedSnapshots) + SumMemory(_persistableCompactedSnapshots);

    /// <summary>
    /// Load all persisted snapshots from catalog and arena files.
    /// </summary>
    public void LoadFromCatalog()
    {
        lock (_catalogLock)
        {
            _catalog.Load();
            _arenaManager.Initialize(_catalog.Entries);

            // Load base snapshots first
            foreach (SnapshotCatalog.CatalogEntry entry in _catalog.Entries)
            {
                if (entry.Type != PersistedSnapshotType.Full) continue;
                LoadSnapshot(entry);
            }

            // Then compacted
            foreach (SnapshotCatalog.CatalogEntry entry in _catalog.Entries)
            {
                if (entry.Type != PersistedSnapshotType.Linked) continue;
                LoadSnapshot(entry);
            }

            _nextId = _catalog.NextId();
        }
    }

    private void LoadSnapshot(SnapshotCatalog.CatalogEntry entry)
    {
        ArenaReservation reservation = _arenaManager.Open(entry.Location);

        PersistedSnapshot[]? referencedSnapshots = null;
        if (entry.Type == PersistedSnapshotType.Linked)
        {
            int[]? refIds = PersistedSnapshot.ReadRefIdsFromMetadata(reservation.GetSpan());
            if (refIds is { Length: > 0 })
            {
                List<PersistedSnapshot> refs = new();
                foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _baseSnapshots)
                {
                    for (int i = 0; i < refIds.Length; i++)
                    {
                        if (kv.Value.Id == refIds[i])
                        {
                            refs.Add(kv.Value);
                            break;
                        }
                    }
                }
                referencedSnapshots = refs.Count > 0 ? refs.ToArray() : null;
            }
        }

        PersistedSnapshot snapshot = new(entry.Id, entry.From, entry.To, entry.Type, reservation, referencedSnapshots);

        if (entry.Type == PersistedSnapshotType.Full)
            _baseSnapshots[entry.To] = snapshot;
        else if (entry.To.BlockNumber - entry.From.BlockNumber == _compactSize)
            _persistableCompactedSnapshots[entry.To] = snapshot;
        else
            _compactedSnapshots[entry.To] = snapshot;
    }

    private Histogram _persistedSnapshotSize = Prometheus.Metrics.CreateHistogram("persisted_snapshot_size", "persisted_snapshot_size");

    /// <summary>
    /// Persist an in-memory snapshot to disk as a base snapshot (keyed by To StateId).
    /// Uses ReserveForWrite/FinalizedWrite to write HSST data directly into mmap'd arena memory (zero-copy).
    /// </summary>
    public void ConvertSnapshotToPersistedSnapshot(Snapshot snapshot)
    {
        int estimatedSize = PersistedSnapshotBuilder.EstimateSize(snapshot);
        ArenaReservation reservation = _arenaManager.ReserveForWrite(estimatedSize);
        int actualSize;
        try
        {
            SpanBufferWriter writer = new(reservation.GetSpan());
            PersistedSnapshotBuilder.Build(snapshot, ref writer);
            _persistedSnapshotSize.Observe(writer.Written);
            actualSize = writer.Written;
        }
        catch
        {
            reservation.Return();
            throw;
        }

        lock (_catalogLock)
        {
            int id = _nextId++;
            SnapshotLocation location = reservation.FinalizedWrite(actualSize);
            _catalog.Add(new SnapshotCatalog.CatalogEntry(id, snapshot.From, snapshot.To, PersistedSnapshotType.Full, location));
            _catalog.Save();

            PersistedSnapshot persisted = new(id, snapshot.From, snapshot.To, PersistedSnapshotType.Full, reservation);
            PersistedSnapshotUtils.ValidatePersistedSnapshot(snapshot, persisted);
            _baseSnapshots[snapshot.To] = persisted;
        }
    }

    /// <summary>
    /// Store a compacted snapshot from a pre-reserved arena buffer (zero-copy path).
    /// Referenced snapshot IDs are the base snapshots whose data is referenced via NodeRefs.
    /// </summary>
    public void AddCompactedSnapshot(StateId from, StateId to, ArenaReservation reservation, int actualSize, HashSet<int> referencedSnapshotIds, bool isPersistable)
    {
        lock (_catalogLock)
        {
            int id = _nextId++;
            SnapshotLocation location = reservation.FinalizedWrite(actualSize);
            _catalog.Add(new SnapshotCatalog.CatalogEntry(id, from, to, PersistedSnapshotType.Linked, location));
            _catalog.Save();

            PersistedSnapshot[]? referencedSnapshots = ResolveReferencedSnapshots(referencedSnapshotIds);
            PersistedSnapshot snapshot = new(id, from, to, PersistedSnapshotType.Linked, reservation, referencedSnapshots);
            if (isPersistable)
                _persistableCompactedSnapshots[to] = snapshot;
            else
                _compactedSnapshots[to] = snapshot;
        }
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
            PersistedSnapshot? snapshot = null;

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
        foreach (PersistedSnapshot snapshot in _compactedSnapshots.Values)
        {
            if (snapshot.From == fromState && snapshot.TryAcquire())
                return snapshot;
        }

        foreach (PersistedSnapshot snapshot in _baseSnapshots.Values)
        {
            if (snapshot.From == fromState && snapshot.TryAcquire())
                return snapshot;
        }

        return null;
    }

    /// <summary>
    /// Prune snapshots with To.BlockNumber before the given state.
    /// </summary>
    public int PruneBefore(StateId stateId)
    {
        lock (_catalogLock)
        {
            int pruned = 0;

            // Collect base snapshot IDs referenced by active compacted snapshots
            HashSet<int> referencedBaseIds = new();
            foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _compactedSnapshots)
            {
                if (kv.Value.To.BlockNumber >= stateId.BlockNumber && kv.Value.ReferencedSnapshotIds is int[] ids)
                {
                    for (int i = 0; i < ids.Length; i++) referencedBaseIds.Add(ids[i]);
                }
            }
            foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _persistableCompactedSnapshots)
            {
                if (kv.Value.To.BlockNumber >= stateId.BlockNumber && kv.Value.ReferencedSnapshotIds is int[] ids)
                {
                    for (int i = 0; i < ids.Length; i++) referencedBaseIds.Add(ids[i]);
                }
            }

            // Prune base snapshots (skip if referenced by an active compacted snapshot)
            List<StateId> baseToRemove = new();
            foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _baseSnapshots)
            {
                if (kv.Value.To.BlockNumber < stateId.BlockNumber && !referencedBaseIds.Contains(kv.Value.Id))
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
            List<StateId> compactedToRemove = new();
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
            List<StateId> persistableToRemove = new();
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

            if (pruned > 0) _catalog.Save();
            return pruned;
        }
    }

    public bool HasBaseSnapshot(in StateId stateId) => _baseSnapshots.ContainsKey(stateId);

    /// <summary>
    /// Look up base snapshots by ID and return them as an array for NodeRef resolution.
    /// </summary>
    private PersistedSnapshot[]? ResolveReferencedSnapshots(ICollection<int> snapshotIds)
    {
        if (snapshotIds is { Count: 0 }) return null;
        List<PersistedSnapshot> result = new();
        foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _baseSnapshots)
        {
            if (snapshotIds.Contains(kv.Value.Id))
                result.Add(kv.Value);
        }
        return result.Count > 0 ? result.ToArray() : null;
    }

    private void RemoveFromCatalog(int snapshotId)
    {
        SnapshotCatalog.CatalogEntry? entry = _catalog.Find(snapshotId);
        if (entry is not null)
        {
            _arenaManager.MarkDead(entry.Location);
            _catalog.Remove(snapshotId);
        }
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
            foreach (PersistedSnapshot snapshot in _baseSnapshots.Values)
                snapshot.Dispose();
            foreach (PersistedSnapshot snapshot in _compactedSnapshots.Values)
                snapshot.Dispose();
            foreach (PersistedSnapshot snapshot in _persistableCompactedSnapshots.Values)
                snapshot.Dispose();
            _baseSnapshots.Clear();
            _compactedSnapshots.Clear();
            _persistableCompactedSnapshots.Clear();
        }
    }
}
