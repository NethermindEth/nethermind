// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Collections;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// One self-contained snapshot bucket for a single persisted <see cref="SnapshotTier"/>: a <c>To</c>-keyed
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> for lock-free point lookups, a block-ordered
/// <see cref="SortedSet{T}"/> of its <c>To</c>s, and running memory/count totals — all guarded by
/// the bucket's own <see cref="Lock"/>. The bucket owns its share of the shared catalog and the
/// process-wide memory/count metrics, so insert/prune/remove are end-to-end here.
/// </summary>
/// <remarks>
/// Totals are read lock-free via <see cref="Interlocked.Read(ref long)"/>; the dictionary serves
/// point lookups lock-free. The lock only serialises ordered-set mutation, catalog writes, and
/// the lease/dispose handoff so a racing prune cannot dispose an entry between insert and return.
/// </remarks>
internal sealed class PersistedSnapshotBucket(ISnapshotCatalog catalog, SnapshotTier tier, ILogger logger)
{
    private readonly ConcurrentDictionary<StateId, PersistedSnapshot> _byTo = new();
    private readonly SortedSet<StateId> _ordered = [];
    private readonly Lock _lock = new();
    private long _memoryBytes;
    private long _count;
    private readonly string _tierName = tier.MetricTierLabel();

    public long MemoryBytes => Interlocked.Read(ref _memoryBytes);
    public long Count => Interlocked.Read(ref _count);

    /// <summary>The greatest <c>To</c> held by this bucket, or <c>null</c> when empty.</summary>
    public StateId? Max
    {
        get { using Lock.Scope scope = _lock.EnterScope(); return _ordered.Count == 0 ? null : _ordered.Max; }
    }

    private PersistedSnapshotLabel LabelFor(PersistedSnapshot snapshot) =>
        new(_tierName, (long)(snapshot.To.BlockNumber - snapshot.From.BlockNumber));

    /// <summary>Live snapshots, for one-off lifecycle iteration (bloom rebuild) at construction.
    /// Enumerates the dictionary directly — does not allocate a Values snapshot.</summary>
    public IEnumerable<PersistedSnapshot> Snapshots
    {
        get
        {
            foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _byTo)
                yield return kv.Value;
        }
    }

    public bool TryGet(in StateId to, [NotNullWhen(true)] out PersistedSnapshot? snapshot) =>
        _byTo.TryGetValue(to, out snapshot);

    public bool ContainsKey(in StateId to) => _byTo.ContainsKey(to);

    /// <summary>
    /// Insert or overwrite the snapshot at <paramref name="to"/>, under this bucket's lock so the
    /// dictionary and the ordered set stay consistent against a concurrent catalog load or racing prune.
    /// </summary>
    public void Set(in StateId to, PersistedSnapshot snapshot)
    {
        using Lock.Scope scope = _lock.EnterScope();
        _byTo[to] = snapshot;
        _ordered.Add(to);
        Interlocked.Add(ref _memoryBytes, snapshot.Size);
        Interlocked.Increment(ref _count);
        PersistedSnapshotLabel label = LabelFor(snapshot);
        Metrics.PersistedSnapshotMemory.AddBy(label, snapshot.Size);
        Metrics.PersistedSnapshotCount.AddBy(label, 1);
    }

    /// <summary>
    /// Like <see cref="Set"/> but also pre-acquires the caller's lease under the same lock, so a
    /// racing prune cannot dispose the entry between insert and return. The catalog entry is written
    /// by the caller, not here.
    /// </summary>
    public void Add(in StateId to, PersistedSnapshot snapshot)
    {
        using Lock.Scope scope = _lock.EnterScope();
        Set(to, snapshot);
        snapshot.AcquireLease();
    }

    public bool Replace(in StateId to, PersistedSnapshot replacement)
    {
        using Lock.Scope scope = _lock.EnterScope();
        if (!_byTo.TryGetValue(to, out PersistedSnapshot? old)) return false;
        replacement.AcquireLease();
        _byTo[to] = replacement;
        old.Dispose();
        return true;
    }

    /// <summary>Remove the entry at <paramref name="to"/> (catalog + index + leases) under this
    /// bucket's lock. Returns <c>true</c> when an entry was present.</summary>
    public bool RemoveExact(in StateId to)
    {
        using Lock.Scope scope = _lock.EnterScope();
        return RemoveLocked(to);
    }

    /// <summary>
    /// Prune the block-ordered prefix whose <c>To.BlockNumber &lt; beforeBlock</c>, removing each
    /// entry (catalog + index + leases) under this bucket's lock.
    /// </summary>
    public void PruneBefore(ulong beforeBlock)
    {
        using Lock.Scope scope = _lock.EnterScope();
        // Materialise the prefix first — the removal loop mutates the ordered set.
        using ArrayPoolList<StateId> toRemove = new(0);
        foreach (StateId to in _ordered)
        {
            if (to.BlockNumber >= beforeBlock) break;
            toRemove.Add(to);
        }
        foreach (StateId to in toRemove) RemoveLocked(to);
    }

    /// <summary>Copy this bucket's <c>To</c>s in the inclusive [<paramref name="min"/>,
    /// <paramref name="max"/>] range into <paramref name="into"/>, under this bucket's lock.</summary>
    public void CollectRange(in StateId min, in StateId max, ISet<StateId> into)
    {
        using Lock.Scope scope = _lock.EnterScope();
        foreach (StateId to in _ordered.GetViewBetween(min, max))
            into.Add(to);
    }

    /// <summary>Mark every live snapshot's files shutdown-preserved, under this bucket's lock.
    /// Must complete across all buckets before any <see cref="DisposeAndClear"/>.</summary>
    public void PersistAllOnShutdown()
    {
        using Lock.Scope scope = _lock.EnterScope();
        foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _byTo)
            kv.Value.PersistOnShutdown();
    }

    /// <summary>Dispose every live snapshot, clear the index, and roll back this bucket's
    /// contribution to the global memory/count gauges. Under this bucket's lock.</summary>
    public void DisposeAndClear()
    {
        using Lock.Scope scope = _lock.EnterScope();
        if (logger.IsDebug && _byTo.Count > 0) logger.Debug($"Releasing {_byTo.Count} persisted snapshot(s) ({_tierName}) on teardown");
        foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _byTo)
        {
            PersistedSnapshotLabel label = LabelFor(kv.Value);
            Metrics.PersistedSnapshotMemory.AddBy(label, -kv.Value.Size);
            Metrics.PersistedSnapshotCount.AddBy(label, -1);
            kv.Value.Dispose();
        }
        _byTo.Clear();
        _ordered.Clear();
        Interlocked.Exchange(ref _memoryBytes, 0);
        Interlocked.Exchange(ref _count, 0);
    }

    /// <summary>
    /// Remove <paramref name="to"/> from the index + catalog, dispose its leases, and roll back
    /// the bucket and global totals (bumping the prune metric). This bucket's lock must be held.
    /// </summary>
    private bool RemoveLocked(in StateId to)
    {
        _ordered.Remove(to);
        if (!_byTo.TryRemove(to, out PersistedSnapshot? snapshot)) return false;
        // Capture depth before Dispose — From/To stay valid on the still-alive object, but the
        // underlying reservation/file leases are released by Dispose. The catalog key scopes the
        // removal to this bucket's entry (the other buckets' entries at the same To carry a
        // different depth and stay put).
        long depth = (long)(to.BlockNumber - snapshot.From.BlockNumber);
        Interlocked.Add(ref _memoryBytes, -snapshot.Size);
        Interlocked.Decrement(ref _count);
        PersistedSnapshotLabel label = LabelFor(snapshot);
        Metrics.PersistedSnapshotMemory.AddBy(label, -snapshot.Size);
        Metrics.PersistedSnapshotCount.AddBy(label, -1);
        Interlocked.Increment(ref Metrics._persistedSnapshotPrunes);
        catalog.Remove(to, depth);
        if (logger.IsDebug) logger.Debug($"Released persisted snapshot {_tierName} {snapshot.From.BlockNumber}->{to.BlockNumber}");
        snapshot.Dispose();
        return true;
    }
}
