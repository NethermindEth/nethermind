// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Manages multiple arena files for snapshot storage. Handles allocation,
/// reading, and dead space tracking. Writes go through <see cref="ArenaWriter"/>
/// backed by FileStream; reads use mmap.
/// </summary>
public sealed class ArenaManager : IArenaManager
{
    private const string ArenaFilePrefix = "arena_";
    private const string DedicatedArenaFilePrefix = "dedicated_";
    private const string ArenaFileExtension = ".bin";
    private const long DefaultDedicatedArenaThreshold = 512L * 1024 * 1024;

    private readonly string _basePath;
    private readonly long _maxArenaSize;
    private readonly long _dedicatedArenaThreshold;
    private readonly bool _fadviseOnEviction;
    // Make it prefer earlier arena.
    private readonly ConcurrentDictionary<int, ArenaFile> _arenas = new();
    private readonly Dictionary<int, long> _frontiers = [];
    private readonly Dictionary<int, long> _deadBytes = [];
    private readonly HashSet<int> _reservedArenas = [];
    private readonly HashSet<int> _standaloneFiles = [];
    private readonly HashSet<int> _mutableArenas = [];
    private readonly Lock _lock = new();
    private readonly PageResidencyTracker _pageTracker;
    // MPSC-used MpmcRingBuffer for queued evictions; null when the tracker is disabled
    // (no pages tracked → no evictions to dispatch).
    private readonly MpmcRingBuffer<long>? _evictionRing;
    private readonly SemaphoreSlim? _evictionWake;
    private readonly CancellationTokenSource? _evictionDrainCts;
    private readonly Task? _evictionDrainTask;
    // 0 = drain may sleep, 1 = at least one item is queued. Producers flip 0→1 and Release; the
    // drain resets it to 0 before draining and re-checks after to close the lost-wakeup race.
    private int _evictionSignal;
    // Lightweight observability — also used by tests. Never decremented.
    private long _evictionsQueued;
    private long _evictionsInlineFallback;
    private long _evictionsSkippedRetouched;
    private long _evictionsDispatched;
    private int _nextArenaId;
    private bool _disposed;

    internal long EvictionsQueued => Volatile.Read(ref _evictionsQueued);
    internal long EvictionsInlineFallback => Volatile.Read(ref _evictionsInlineFallback);
    internal long EvictionsSkippedRetouched => Volatile.Read(ref _evictionsSkippedRetouched);
    internal long EvictionsDispatched => Volatile.Read(ref _evictionsDispatched);

    public PageResidencyTracker PageTracker => _pageTracker;

    public int ArenaFileCount
    {
        get { lock (_lock) return _arenas.Count; }
    }

    public long ArenaMappedBytes
    {
        get
        {
            lock (_lock)
            {
                long sum = 0;
                foreach (KeyValuePair<int, ArenaFile> kv in _arenas) sum += kv.Value.MappedSize;
                return sum;
            }
        }
    }

    public ArenaManager(string basePath, long pageCacheBytes, long maxArenaSize = 1L * 1024 * 1024 * 1024, bool fadviseOnEviction = false, long dedicatedArenaThreshold = DefaultDedicatedArenaThreshold)
    {
        _basePath = basePath;
        _maxArenaSize = maxArenaSize;
        _dedicatedArenaThreshold = dedicatedArenaThreshold;
        _fadviseOnEviction = fadviseOnEviction;
        Directory.CreateDirectory(basePath);
        _pageTracker = PageResidencyTracker.FromByteBudget(pageCacheBytes);

        // Eviction queue is sized at 10% of the tracker's slot capacity (rounded up to the next
        // power of two, floored at 64). With the tracker disabled (capacity 0) there are no
        // evictions to dispatch — skip the ring + drain task entirely so we don't pay for an
        // idle Task.
        if (_pageTracker.MaxCapacity > 0)
        {
            int ringCapacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(64, _pageTracker.MaxCapacity / 10));
            _evictionRing = new MpmcRingBuffer<long>(ringCapacity);
            _evictionWake = new SemaphoreSlim(0, int.MaxValue);
            _evictionDrainCts = new CancellationTokenSource();
            _evictionDrainTask = Task.Run(() => DrainEvictionsAsync(_evictionDrainCts.Token));
        }
    }

    /// <summary>
    /// Initialize from existing arena files and catalog entries.
    /// Computes allocation frontiers and dead bytes per arena.
    /// </summary>
    public void Initialize(IReadOnlyList<SnapshotCatalog.CatalogEntry> entries)
    {
        lock (_lock)
        {
            // Open existing arena files
            foreach (string file in Directory.GetFiles(_basePath, $"*{ArenaFileExtension}"))
            {
                string fileName = Path.GetFileName(file);
                bool isDedicated = fileName.StartsWith(DedicatedArenaFilePrefix, StringComparison.Ordinal);
                bool isArena = fileName.StartsWith(ArenaFilePrefix, StringComparison.Ordinal);
                if (!isDedicated && !isArena) continue;

                int arenaId = ParseArenaId(file, isDedicated);
                if (arenaId < 0) continue;

                // Determine mapped size: use file length if non-zero, otherwise default
                long fileLength = new FileInfo(file).Length;
                long mappedSize = fileLength > 0 ? fileLength : _maxArenaSize;

                ArenaFile arena = new(arenaId, file, mappedSize);
                _arenas[arenaId] = arena;
                _frontiers[arenaId] = 0;
                _deadBytes[arenaId] = 0;
                _nextArenaId = Math.Max(_nextArenaId, arenaId + 1);

                if (isDedicated)
                    _standaloneFiles.Add(arenaId);
            }

            // Compute frontiers and live sizes from catalog
            Dictionary<int, long> liveSizes = [];
            foreach (SnapshotCatalog.CatalogEntry entry in entries)
            {
                int aid = entry.Location.ArenaId;
                long end = entry.Location.Offset + entry.Location.Size;

                if (!_frontiers.TryGetValue(aid, out long frontier) || end > frontier)
                    _frontiers[aid] = end;

                liveSizes.TryGetValue(aid, out long live);
                liveSizes[aid] = live + entry.Location.Size;
            }

            // Dead bytes = frontier - live sizes
            foreach (KeyValuePair<int, long> kv in _frontiers)
            {
                liveSizes.TryGetValue(kv.Key, out long live);
                _deadBytes[kv.Key] = kv.Value - live;
            }
        }
    }

    /// <summary>
    /// Create an <see cref="ArenaWriter"/> for buffered writes.
    /// The arena is marked as reserved until <see cref="CompleteWrite"/> or <see cref="CancelWrite"/>.
    /// </summary>
    public ArenaWriter CreateWriter(long estimatedSize, string tag)
    {
        lock (_lock)
        {
            ArenaFile file = estimatedSize >= _dedicatedArenaThreshold
                ? CreateArenaFile(estimatedSize, dedicated: true)
                : GetOrCreateArena(estimatedSize);
            long offset = _frontiers[file.Id];
            _reservedArenas.Add(file.Id);
            FileStream stream = file.CreateWriteStream(offset);
            return new ArenaWriter(this, file.Id, offset, stream, tag);
        }
    }

    /// <summary>
    /// Complete a buffered write. Updates frontier and returns location + reservation.
    /// Dedicated arenas are pre-sized to the writer's estimate; trim the file down
    /// to the actual frontier so the on-disk length and mmap footprint match what
    /// was written (the estimate is an upper bound and is often an overcount).
    /// </summary>
    public (SnapshotLocation Location, ArenaReservation Reservation) CompleteWrite(int arenaId, long startOffset, long actualSize, string tag)
    {
        lock (_lock)
        {
            long newFrontier = startOffset + actualSize;
            _frontiers[arenaId] = newFrontier;
            _reservedArenas.Remove(arenaId);

            if (newFrontier > 0
                && _standaloneFiles.Contains(arenaId)
                && _arenas.TryGetValue(arenaId, out ArenaFile? oldFile)
                && newFrontier < oldFile.MappedSize)
            {
                string path = oldFile.Path;
                oldFile.Dispose();
                using (Microsoft.Win32.SafeHandles.SafeFileHandle h =
                    File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                    RandomAccess.SetLength(h, newFrontier);
                _arenas[arenaId] = new ArenaFile(arenaId, path, newFrontier);
            }

            SnapshotLocation location = new(arenaId, startOffset, actualSize);
            _arenas.TryGetValue(arenaId, out ArenaFile? arenaFile);
            ArenaReservation reservation = new(this, arenaFile, arenaId, startOffset, actualSize, tag);
            return (location, reservation);
        }
    }

    /// <summary>
    /// Cancel a buffered write. Unmarks arena as reserved.
    /// For dedicated arenas, deletes the file; for shared arenas, data past frontier is ignored.
    /// </summary>
    public void CancelWrite(int arenaId, long startOffset)
    {
        lock (_lock)
        {
            _reservedArenas.Remove(arenaId);

            if (_standaloneFiles.Contains(arenaId))
            {
                _standaloneFiles.Remove(arenaId);
                if (_arenas.TryRemove(arenaId, out ArenaFile? file))
                {
                    file.Dispose();
                    File.Delete(file.Path);
                }
                _frontiers.Remove(arenaId);
                _deadBytes.Remove(arenaId);
            }
        }
    }

    /// <summary>
    /// Open an existing snapshot location as an <see cref="ArenaReservation"/> for zero-copy reads.
    /// </summary>
    public ArenaReservation Open(in SnapshotLocation location, string tag)
    {
        _arenas.TryGetValue(location.ArenaId, out ArenaFile? arenaFile);
        return new(this, arenaFile, location.ArenaId, location.Offset, location.Size, tag);
    }

    /// <summary>
    /// Get a read-only span for the reservation's data region.
    /// </summary>
    public ReadOnlySpan<byte> GetSpan(ArenaReservation reservation) =>
        _arenas[reservation.ArenaId].GetSpan(reservation.Offset, reservation.Size);

    public unsafe void GetReservationPointer(ArenaReservation reservation, out byte* dataPtr, out long size)
    {
        ArenaFile arena = _arenas[reservation.ArenaId];
        dataPtr = arena.BasePtr + reservation.Offset;
        size = reservation.Size;
    }

    public IArenaWholeView OpenWholeView(ArenaReservation reservation)
    {
        lock (_lock)
        {
            return _arenas[reservation.ArenaId].OpenWholeView(reservation.Offset, reservation.Size);
        }
    }

    /// <summary>
    /// Mmap a fresh read view over the just-written range. The arena file is opened
    /// <see cref="FileShare.ReadWrite"/> with a parallel mmap (<see cref="ArenaFile"/>),
    /// so the bytes are visible to the read view as soon as the writer's stream has
    /// been flushed (caller's responsibility).
    /// </summary>
    public IArenaWholeView OpenPendingView(int arenaId, long absoluteOffset, long size)
    {
        lock (_lock)
        {
            return _arenas[arenaId].OpenWholeView(absoluteOffset, size);
        }
    }

    /// <summary>
    /// Mark space as dead for compaction tracking.
    /// </summary>
    public void MarkDead(in SnapshotLocation location)
    {
        lock (_lock)
        {
            // After Dispose, on-disk files must be preserved for the next session — skip
            // dead-byte accounting and file deletion entirely.
            if (_disposed) return;
            _deadBytes.TryGetValue(location.ArenaId, out long dead);
            long totalDead = dead + location.Size;
            _deadBytes[location.ArenaId] = totalDead;

            if (totalDead >= _frontiers[location.ArenaId])
            {
                // All data is dead: dispose and delete the file
                _standaloneFiles.Remove(location.ArenaId);
                _mutableArenas.Remove(location.ArenaId);
                if (_arenas.TryRemove(location.ArenaId, out ArenaFile? file))
                {
                    file.Dispose();
                    File.Delete(file.Path);
                }
                _frontiers.Remove(location.ArenaId);
                _deadBytes.Remove(location.ArenaId);
            }
            else if (_arenas.TryGetValue(location.ArenaId, out ArenaFile? arena))
            {
                arena.AdviseDontNeed(location.Offset, location.Size);
                if (_fadviseOnEviction)
                    arena.FadviseDontNeed(location.Offset, location.Size);
            }
        }
        ForgetTrackerRange(location.ArenaId, location.Offset, location.Size);
    }

    public void AdviseDontNeed(ArenaReservation reservation)
    {
        lock (_lock)
        {
            if (_arenas.TryGetValue(reservation.ArenaId, out ArenaFile? arena))
                arena.AdviseDontNeed(reservation.Offset, reservation.Size);
        }
        ForgetTrackerRange(reservation.ArenaId, reservation.Offset, reservation.Size);
    }

    // Drop tracker entries for every fully-covered OS page in [byteOffset, byteOffset+byteSize).
    // Mirrors ArenaFile.AdviseDontNeed's page-rounding (offset rounded up, end rounded down).
    // Runs outside the manager lock — the tracker is independent of arena lifecycle.
    private void ForgetTrackerRange(int arenaId, long byteOffset, long byteSize)
    {
        if (_pageTracker.MaxCapacity == 0 || byteSize <= 0) return;
        int pageSize = Environment.SystemPageSize;
        long startPage = (byteOffset + pageSize - 1) / pageSize;
        long endPageExclusive = (byteOffset + byteSize) / pageSize;
        for (long p = startPage; p < endPageExclusive; p++)
            _pageTracker.Forget(arenaId, (int)p);
    }

    public void Touch(ArenaReservation reservation, long subOffset, long size)
    {
        if (_arenas.TryGetValue(reservation.ArenaId, out ArenaFile? arena))
            arena.Touch(reservation.Offset + subOffset, size);
    }

    public int RandomRead(ArenaReservation reservation, long subOffset, Span<byte> destination)
    {
        // Intentionally does not touch the page residency tracker: the whole point of
        // this path is to avoid faulting the referenced arena's pages into our resident
        // set.
        if (!_arenas.TryGetValue(reservation.ArenaId, out ArenaFile? arena)) return 0;
        return arena.RandomRead(reservation.Offset + subOffset, destination);
    }

    public void QueueEviction(int arenaId, int pageIdx)
    {
        // Disabled tracker (no ring) — nothing to do; the producer wouldn't even reach here
        // because TryTouch always returns Hit, but stay defensive for direct callers.
        if (_evictionRing is null) return;

        long packed = ((long)(uint)arenaId << 32) | (uint)pageIdx;
        if (_evictionRing.TryEnqueue(packed))
        {
            Interlocked.Increment(ref _evictionsQueued);
            // Wake the drain only on the empty→non-empty edge; subsequent enqueues piggy-back
            // on the in-flight wake-up.
            if (Interlocked.Exchange(ref _evictionSignal, 1) == 0)
                _evictionWake!.Release();
            return;
        }

        // Ring full — fall back to inline dispatch so the eviction is not lost. Bursts large
        // enough to fill 10% of the residency cap should be rare; if seen in practice, raise
        // the ring fraction or the per-arena budget.
        Interlocked.Increment(ref _evictionsInlineFallback);
        DispatchEvictionInline(arenaId, pageIdx);
    }

    private async Task DrainEvictionsAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Reset the signal *before* draining; if a producer enqueues mid-drain it will
                // flip the flag back to 1 and the post-drain check picks it up.
                Volatile.Write(ref _evictionSignal, 0);
                while (_evictionRing!.TryDequeue(out long packed))
                    DispatchOneEviction(packed);

                if (Volatile.Read(ref _evictionSignal) != 0) continue;
                await _evictionWake!.WaitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown — drain leftovers happens in Dispose.
        }
    }

    private void DispatchOneEviction(long packed)
    {
        int arenaId = (int)(packed >> 32);
        int pageIdx = (int)packed;
        // Re-check residency: if the page returned to the working set between enqueue and
        // drain, skip the syscall — punishing it would just force a re-fault on the next read.
        if (_pageTracker.ContainsPage(arenaId, pageIdx))
        {
            Interlocked.Increment(ref _evictionsSkippedRetouched);
            return;
        }
        Interlocked.Increment(ref _evictionsDispatched);
        DispatchEvictionInline(arenaId, pageIdx);
    }

    private void DispatchEvictionInline(int arenaId, int pageIdx)
    {
        if (!_arenas.TryGetValue(arenaId, out ArenaFile? arena)) return;
        int pageSize = Environment.SystemPageSize;
        long offset = (long)pageIdx * pageSize;
        arena.AdviseDontNeed(offset, pageSize);
        if (_fadviseOnEviction)
            arena.FadviseDontNeed(offset, pageSize);
    }

    private ArenaFile GetOrCreateArena(long requiredSize)
    {
        // Scan only mutable arenas; remove any that can't fit (they become permanently read-only)
        List<int>? toRemove = null;
        ArenaFile? result = null;
        foreach (int id in _mutableArenas)
        {
            if (_reservedArenas.Contains(id)) continue;
            long frontier = _frontiers.GetValueOrDefault(id);
            if (frontier + requiredSize <= _arenas[id].MappedSize)
            {
                result = _arenas[id];
                break;
            }

            (toRemove ??= []).Add(id);
        }

        if (toRemove is not null)
        {
            foreach (int id in toRemove)
                _mutableArenas.Remove(id);
        }

        return result ?? CreateArenaFile();
    }

    private ArenaFile CreateArenaFile(long mappedSize = 0, bool dedicated = false)
    {
        if (mappedSize == 0) mappedSize = _maxArenaSize;
        int id = _nextArenaId++;
        string prefix = dedicated ? DedicatedArenaFilePrefix : ArenaFilePrefix;
        string path = Path.Combine(_basePath, $"{prefix}{id:D4}{ArenaFileExtension}");
        ArenaFile arena = new(id, path, mappedSize);
        _arenas[id] = arena;
        _frontiers[id] = 0;
        _deadBytes[id] = 0;
        if (dedicated) _standaloneFiles.Add(id);
        else _mutableArenas.Add(id);
        return arena;
    }

    private static int ParseArenaId(string filePath, bool dedicated)
    {
        string fileName = Path.GetFileNameWithoutExtension(filePath);
        string prefix = dedicated ? DedicatedArenaFilePrefix : ArenaFilePrefix;
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)) return -1;
        return int.TryParse(fileName.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int id) ? id : -1;
    }

    public void Dispose()
    {
        // Idempotent — owners higher up may also Dispose us through their own teardown.
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        // Stop the drain task first so it doesn't race with arena disposal below.
        _evictionDrainCts?.Cancel();
        try { _evictionWake?.Release(); } catch (ObjectDisposedException) { /* concurrent dispose */ }
        try { _evictionDrainTask?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException)) { /* expected */ }

        // Drain any leftovers synchronously; the syscalls are cheap enough that we'd rather
        // pay the cost than leave kernel pages cached for a process about to exit.
        if (_evictionRing is not null)
            while (_evictionRing.TryDequeue(out long packed))
                DispatchOneEviction(packed);

        _evictionWake?.Dispose();
        _evictionDrainCts?.Dispose();

        lock (_lock)
        {
            foreach (KeyValuePair<int, ArenaFile> kv in _arenas)
                kv.Value.Dispose();
            _arenas.Clear();
        }
        _pageTracker.Dispose();
    }
}
