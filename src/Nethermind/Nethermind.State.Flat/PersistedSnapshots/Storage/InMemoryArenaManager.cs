// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// RAM-backed <see cref="IArenaManager"/>: each snapshot slice lives in its own growable native
/// buffer (<see cref="ArenaFile.CreateInMemory"/>) instead of being packed into an mmap'd on-disk
/// arena file. Selected by <c>FlatNodeStorageInMemoryArena</c> to demote aged base snapshots into
/// RAM. One file per slice (all "dedicated"), so there is no packing pool; the page-residency
/// tracker is zero-capacity and every disk-only operation (punch-hole, fadvise, fsync, eviction)
/// is a no-op per the <see cref="IArenaManager"/> contract. The tier is session-ephemeral —
/// <see cref="Initialize"/> has nothing to rehydrate across a restart.
/// </summary>
public sealed class InMemoryArenaManager : IArenaManager
{
    private readonly ConcurrentDictionary<int, ArenaFile> _arenas = new();
    // Zero-capacity tracker: TryTouch is a no-op returning Hit, so reservations never queue evictions.
    private readonly PageResidencyTracker _pageTracker = PageResidencyTracker.FromByteBudget(0);
    private readonly Lock _lock = new();
    private int _nextArenaId;
    private bool _disposed;

    public PageResidencyTracker PageTracker => _pageTracker;

    /// <summary>No-op: the RAM tier holds nothing across a restart, so there is nothing to rehydrate.</summary>
    public void Initialize(IReadOnlyList<CatalogEntry> entries) { }

    public ArenaWriter CreateWriter(long estimatedSize, bool small = false)
    {
        using Lock.Scope scope = _lock.EnterScope();
        int id = _nextArenaId++;
        // Size the per-slice buffer to the estimate; NativeArenaBuffer grows if the write overruns it.
        ArenaFile file = ArenaFile.CreateInMemory(id, Math.Max(estimatedSize, 1), small);
        _arenas[id] = file;
        file.ReportAdded();
        Stream stream = file.CreateWriteStream(0);
        // Every slice owns its file (no packing), which is exactly the "dedicated" writer shape.
        return new ArenaWriter(this, file, dedicated: true, 0, stream);
    }

    public ArenaReservation Open(in SnapshotLocation location)
    {
        if (!_arenas.TryGetValue(location.ArenaId, out ArenaFile? arenaFile))
            throw new InvalidOperationException($"Arena {location.ArenaId} is not registered with this manager.");
        return new ArenaReservation(this, arenaFile, location.ArenaId, location.Offset, location.Size);
    }

    public void OnWriteCompleted(ArenaFile file, long newFrontier, bool hasHeadroom)
    {
        using Lock.Scope scope = _lock.EnterScope();
        file.WriterActive = false;
        file.Frontier = newFrontier;
        long delta = file.Frontier - file.ReportedFrontier;
        if (delta != 0)
        {
            file.ReportedFrontier = file.Frontier;
            Interlocked.Add(ref Metrics._arenaAllocatedBytes, delta);
        }
    }

    // In-memory slices are always dedicated, so a shared-cancel is never reached in practice; drop the
    // file defensively so a future change can't leak one.
    public void OnWriteCancelledShared(ArenaFile file)
    {
        using Lock.Scope scope = _lock.EnterScope();
        file.WriterActive = false;
        if (_arenas.TryRemove(file.Id, out _))
        {
            file.ReportRemoved();
            file.Dispose();
        }
    }

    public void OnWriteCancelledDedicated(ArenaFile file)
    {
        // The writer already dropped the manager's ref (freeing the buffer); just clear the dict + metric.
        using Lock.Scope scope = _lock.EnterScope();
        _arenas.TryRemove(file.Id, out _);
        file.ReportRemoved();
    }

    public bool MarkDead(ArenaFile file, long deadSize)
    {
        using Lock.Scope scope = _lock.EnterScope();
        if (_disposed) return false;
        file.DeadBytes += deadSize;
        if (file.DeadBytes < file.Frontier || file.WriterActive) return true;
        if (_arenas.TryRemove(file.Id, out _))
        {
            file.ReportRemoved();
            file.Dispose();
        }
        return false;
    }

    // Disk-only surface — no-ops for a RAM arena (see the IArenaManager doc comments).
    public bool TryPunchHole(ArenaFile file, long offset, long size) => false;
    public void ForgetTrackerRange(int arenaId, long byteOffset, long byteSize) { }
    public void QueueEviction(int arenaId, uint pageIdx) { }

    public void Dispose()
    {
        using Lock.Scope scope = _lock.EnterScope();
        if (_disposed) return;
        _disposed = true;
        foreach (KeyValuePair<int, ArenaFile> kv in _arenas)
        {
            kv.Value.ReportRemoved();
            kv.Value.Dispose();
        }
        _arenas.Clear();
        _pageTracker.Dispose();
    }
}
