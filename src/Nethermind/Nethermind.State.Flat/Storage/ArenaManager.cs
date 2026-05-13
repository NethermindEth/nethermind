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
    private readonly PersistedSnapshotTier _tier;
    // Make it prefer earlier arena.
    private readonly ConcurrentDictionary<int, ArenaFile> _arenas = new();
    private readonly HashSet<int> _standaloneFiles = [];
    // Shared (non-dedicated) arenas with headroom for further packing AND not currently
    // held by a writer. A writer reserves a file by removing it from this set; the writer's
    // Complete / Cancel re-adds it (if room remains). Same pattern as BlobArenaManager.
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

    public ArenaManager(string basePath, long pageCacheBytes, long maxArenaSize = 1L * 1024 * 1024 * 1024, bool fadviseOnEviction = false, long dedicatedArenaThreshold = DefaultDedicatedArenaThreshold, PersistedSnapshotTier? tier = null)
    {
        _basePath = basePath;
        _maxArenaSize = maxArenaSize;
        _dedicatedArenaThreshold = dedicatedArenaThreshold;
        _fadviseOnEviction = fadviseOnEviction;
        // Default to Small for tests/benchmarks that don't care; FlatWorldStateModule
        // passes the actual tier explicitly.
        _tier = tier ?? PersistedSnapshotTier.Small;
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
                _nextArenaId = Math.Max(_nextArenaId, arenaId + 1);
                OnArenaAdded(mappedSize);

                if (isDedicated)
                    _standaloneFiles.Add(arenaId);
            }

            // Compute frontiers (max end-offset of any slice referencing the arena) and live
            // sizes from the catalog. Entries pointing at arena ids we didn't load on disk
            // are dropped silently — the catalog is the slower-moving authority but the
            // on-disk file set is what we can actually serve.
            Dictionary<int, long> liveSizes = [];
            foreach (SnapshotCatalog.CatalogEntry entry in entries)
            {
                int aid = entry.Location.ArenaId;
                if (!_arenas.TryGetValue(aid, out ArenaFile? arena)) continue;
                long end = entry.Location.Offset + entry.Location.Size;
                if (end > arena.Frontier) arena.Frontier = end;

                liveSizes.TryGetValue(aid, out long live);
                liveSizes[aid] = live + entry.Location.Size;
            }

            // Dead bytes = frontier - live sizes (stored on the file itself)
            foreach (KeyValuePair<int, ArenaFile> kv in _arenas)
            {
                liveSizes.TryGetValue(kv.Key, out long live);
                kv.Value.DeadBytes = kv.Value.Frontier - live;
            }
        }
    }

    /// <summary>
    /// Create an <see cref="ArenaWriter"/> for buffered writes. The arena is marked as
    /// reserved until the writer's <see cref="ArenaWriter.Complete"/> or
    /// <see cref="ArenaWriter.Dispose"/> fires. The writer owns the file ref for the
    /// duration of the write and signals back via <see cref="OnWriteCompleted"/> /
    /// <see cref="OnWriteCancelledShared"/> / <see cref="OnWriteCancelledDedicated"/>.
    /// </summary>
    public ArenaWriter CreateWriter(long estimatedSize, string tag)
    {
        lock (_lock)
        {
            bool dedicated = estimatedSize >= _dedicatedArenaThreshold;
            ArenaFile file = dedicated
                ? CreateArenaFile(estimatedSize, dedicated: true)
                : GetOrCreateArena(estimatedSize);
            long offset = file.Frontier;
            // Reserve: remove from the mutable pool so no concurrent CreateWriter picks
            // the same file. The writer's OnWriteCompleted / OnWriteCancelledShared
            // re-adds the id if there's still room. Dedicated files never enter the
            // mutable pool (they live in _standaloneFiles).
            if (!dedicated) _mutableArenas.Remove(file.Id);
            FileStream stream = file.CreateWriteStream(offset);
            return new ArenaWriter(this, file, dedicated, offset, stream, tag);
        }
    }

    /// <summary>
    /// Bookkeeping after <see cref="ArenaWriter.Complete"/>. The writer has already set
    /// <see cref="ArenaFile.Frontier"/> and (if dedicated) called <see cref="ArenaFile.Truncate"/>;
    /// the manager does NOT touch the file here. <paramref name="hasHeadroom"/> is true for
    /// shared writes whose post-frontier still leaves room for further packing.
    /// </summary>
    internal void OnWriteCompleted(int arenaId, bool hasHeadroom, long resizeDelta)
    {
        lock (_lock)
        {
            if (hasHeadroom) _mutableArenas.Add(arenaId);
            if (resizeDelta != 0) OnArenaResized(resizeDelta);
        }
    }

    /// <summary>
    /// Bookkeeping after a cancelled write on a shared (non-dedicated) arena: return the id
    /// to the mutable pool (the writer didn't advance the frontier, so by construction it
    /// still has the same headroom it had when picked).
    /// </summary>
    internal void OnWriteCancelledShared(int arenaId)
    {
        lock (_lock) _mutableArenas.Add(arenaId);
    }

    /// <summary>
    /// Bookkeeping after a cancelled write on a dedicated arena. The writer has already
    /// dropped the file's manager-ref (triggering <see cref="ArenaFile.CleanUp"/> →
    /// close + delete on disk); the manager just clears its dict / state and updates
    /// the byte metric.
    /// </summary>
    internal void OnWriteCancelledDedicated(int arenaId, long mappedSize)
    {
        lock (_lock)
        {
            _standaloneFiles.Remove(arenaId);
            _arenas.TryRemove(arenaId, out _);
            OnArenaRemoved(mappedSize);
        }
    }

    /// <summary>
    /// Open an existing snapshot location as an <see cref="ArenaReservation"/> for zero-copy reads.
    /// Lookup + lease acquisition happens under the manager's lock so a concurrent
    /// <see cref="MarkDead(ArenaFile, long)"/> can't tear the file down mid-construction. If the
    /// file has already started its CleanUp the reservation's ctor surfaces an
    /// <see cref="InvalidOperationException"/> from its <see cref="ArenaFile.TryAcquireLease"/>.
    /// </summary>
    public ArenaReservation Open(in SnapshotLocation location, string tag)
    {
        lock (_lock)
        {
            if (!_arenas.TryGetValue(location.ArenaId, out ArenaFile? arenaFile))
                throw new InvalidOperationException($"Arena {location.ArenaId} is not registered with this manager.");
            return new ArenaReservation(this, arenaFile, location.ArenaId, location.Offset, location.Size, tag);
        }
    }

    /// <summary>
    /// Mark <paramref name="deadSize"/> bytes of <paramref name="file"/> as dead and, if the
    /// file's dead-byte total has caught up with its frontier, drop the manager's dict ref so
    /// the file self-cleans once its last reservation releases its lease. The caller (typically
    /// <see cref="ArenaReservation.CleanUp"/>) already holds the file ref and handles file-side
    /// ops (<c>madvise</c> / optional <c>posix_fadvise</c>) and tracker-forget itself — this
    /// method's sole job is the atomic set/dict/metric mutation that needs the manager lock.
    /// </summary>
    public void MarkDead(ArenaFile file, long deadSize)
    {
        lock (_lock)
        {
            // After Dispose, on-disk files must be preserved for the next session — skip
            // dead-byte accounting and file deletion entirely.
            if (_disposed) return;
            file.DeadBytes += deadSize;
            if (file.DeadBytes < file.Frontier) return;
            _standaloneFiles.Remove(file.Id);
            _mutableArenas.Remove(file.Id);
            if (_arenas.TryRemove(file.Id, out _))
            {
                OnArenaRemoved(file.MappedSize);
                file.Dispose();
            }
        }
    }

    /// <summary>
    /// Whether <see cref="ArenaReservation.CleanUp"/> should also issue a
    /// <c>posix_fadvise(POSIX_FADV_DONTNEED)</c> after the <c>madvise(MADV_DONTNEED)</c>.
    /// Mirrors the <c>fadviseOnEviction</c> ctor argument.
    /// </summary>
    public bool FadviseOnEviction => _fadviseOnEviction;

    // Drop tracker entries for every fully-covered OS page in [byteOffset, byteOffset+byteSize).
    // Mirrors ArenaFile.AdviseDontNeed's page-rounding (offset rounded up, end rounded down).
    // Runs outside the manager lock — the tracker is independent of arena lifecycle.
    public void ForgetTrackerRange(int arenaId, long byteOffset, long byteSize)
    {
        if (_pageTracker.MaxCapacity == 0 || byteSize <= 0) return;
        int pageSize = Environment.SystemPageSize;
        long startPage = (byteOffset + pageSize - 1) / pageSize;
        long endPageExclusive = (byteOffset + byteSize) / pageSize;
        for (long p = startPage; p < endPageExclusive; p++)
            _pageTracker.Forget(arenaId, (int)p);
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
        // Scan mutable arenas (files in this set are by definition not currently held by
        // a writer — reservation == removal from _mutableArenas). Files that can't fit are
        // pruned (they become permanently read-only from the manager's POV).
        List<int>? toRemove = null;
        ArenaFile? result = null;
        foreach (int id in _mutableArenas)
        {
            ArenaFile candidate = _arenas[id];
            if (candidate.Frontier + requiredSize <= candidate.MappedSize)
            {
                result = candidate;
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
        if (dedicated) _standaloneFiles.Add(id);
        // Fresh shared file isn't added to _mutableArenas — the writer that just took it
        // is its "owner". The writer's Complete / Cancel adds it (if room remains).
        OnArenaAdded(mappedSize);
        return arena;
    }

    // Push-style gauge updates. Called under _lock at every file add / remove / resize site so
    // Metrics.ArenaFileCountByTier / ArenaMappedBytesByTier stay consistent with _arenas without
    // periodic iteration. ConcurrentDictionary.AddOrUpdate is atomic.
    private void OnArenaAdded(long mappedSize)
    {
        Metrics.ArenaFileCountByTier.AddOrUpdate(_tier,
            static (_, _) => 1L, static (_, c, _) => c + 1, mappedSize);
        Metrics.ArenaMappedBytesByTier.AddOrUpdate(_tier,
            static (_, m) => m, static (_, b, m) => b + m, mappedSize);
    }

    private void OnArenaRemoved(long mappedSize)
    {
        Metrics.ArenaFileCountByTier.AddOrUpdate(_tier,
            static (_, _) => 0L, static (_, c, _) => Math.Max(0, c - 1), mappedSize);
        Metrics.ArenaMappedBytesByTier.AddOrUpdate(_tier,
            static (_, _) => 0L, static (_, b, m) => Math.Max(0, b - m), mappedSize);
    }

    private void OnArenaResized(long delta) =>
        Metrics.ArenaMappedBytesByTier.AddOrUpdate(_tier,
            static (_, d) => d, static (_, b, d) => b + d, delta);

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
            {
                OnArenaRemoved(kv.Value.MappedSize);
                kv.Value.Dispose();
            }
            _arenas.Clear();
        }
        _pageTracker.Dispose();
    }
}
