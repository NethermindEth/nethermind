// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// File pool for trie-node RLP bytes. Standalone — owns its own file pool, with no
/// dependency on <see cref="ArenaManager"/> or <see cref="IArenaManager"/>. Each known
/// blob file is a refcounted <see cref="BlobArenaFile"/>; the manager's array slot is
/// the file's initial lease (count=1), the writer holds an additional one for the
/// duration of <see cref="BlobArenaWriter"/>, and each leased
/// <see cref="PersistedSnapshots.PersistedSnapshot"/> takes another. The on-disk file is
/// deleted by the file's own <see cref="BlobArenaFile.CleanUp"/> when its refcount hits
/// zero (typically at manager shutdown or in <see cref="SweepUnreferenced"/>); the
/// per-file <see cref="BlobArenaFile.PersistOnShutdown"/> flag overrides delete for files
/// still referenced by loaded snapshots.
///
/// <para>
/// <b>One id per file.</b> A <c>BlobArenaId</c> is the file's stable numeric id
/// (narrowed to <see cref="ushort"/>) — many writers across many base snapshots append
/// into the same file over its lifetime, claiming the file for write via the
/// <c>_mutableFiles</c> packing pool and releasing on Complete. A new id is only minted
/// when no existing file has headroom; with a typical 1 GiB max file size, the count stays
/// well below 65535.
/// </para>
///
/// <para>
/// <b>Reads</b> go through each file's read-only mmap; the resident working set is bounded by
/// a <see cref="PageResidencyTracker"/> that issues <c>madvise(MADV_DONTNEED)</c> on evicted
/// pages — the eviction syscall runs off the read thread on a background drain of an MPSC ring,
/// mirroring the metadata <see cref="ArenaManager"/>. <see cref="TouchBlobPage"/> records each
/// page access on the hot path.
/// </para>
///
/// <para>
/// <b>Storage:</b> a flat <see cref="BlobArenaFile"/>?[ushort.MaxValue + 1] array indexed
/// by id. O(1) lookup, no hash, no concurrent-dictionary overhead. Memory footprint:
/// 65 536 × 8 B ≈ 512 KiB per manager.
/// </para>
/// </summary>
public sealed class BlobArenaManager : IBlobArenaManager
{
    /// <summary>Default page-residency-tracker budget when none is configured: 1 GiB.</summary>
    public const long DefaultBlobPageCacheBytes = 1L << 30;

    private const string BlobFilePrefix = "blob_";
    private const string BlobFileExtension = ".bin";

    private readonly string _basePath;
    private readonly long _maxFileSize;
    private readonly PersistedSnapshotTier _tier;
    private readonly Lock _lock = new();
    // Indexed by blob arena id. Null slot = no file. Reads (TryLeaseFile lookup) are
    // unlocked — reference-slot reads are atomic in the CLR memory model. Slot mutations
    // (insert / null) happen under _lock alongside _mutableFiles.
    private readonly BlobArenaFile?[] _files = new BlobArenaFile?[ushort.MaxValue + 1];
    // Files that still have headroom for further packing AND are not currently held by
    // a writer. A writer reserves a file by removing it from this set; Complete / Cancel
    // re-add it (if room remains). Protected by _lock.
    private readonly HashSet<ushort> _mutableFiles = [];
    private int _nextFileId;
    private bool _disposed;

    // --- Page-residency tracker + eviction dispatch (mirrors ArenaManager) ---
    private readonly PageResidencyTracker _pageTracker;
    // 1s tick that mirrors _pageTracker.ResidentBytes into Metrics.BlobPageTrackerResidentBytesByTier.
    // Null when the tracker is disabled (no residency to track).
    private readonly Timer? _metricsTimer;
    // MPSC-used MpmcRingBuffer for queued evictions; null when the tracker is disabled.
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

    internal long EvictionsQueued => Volatile.Read(ref _evictionsQueued);
    internal long EvictionsInlineFallback => Volatile.Read(ref _evictionsInlineFallback);
    internal long EvictionsSkippedRetouched => Volatile.Read(ref _evictionsSkippedRetouched);
    internal long EvictionsDispatched => Volatile.Read(ref _evictionsDispatched);

    public PageResidencyTracker PageTracker => _pageTracker;

    /// <summary>
    /// Construct a blob arena manager rooted at <paramref name="basePath"/> with a per-file
    /// size cap of <paramref name="maxFileSize"/>. <paramref name="tier"/> is the
    /// pool-tier label; passed through to every <see cref="BlobArenaFile"/> for its
    /// <see cref="Metrics.BlobFileCountByTier"/> / <see cref="Metrics.BlobAllocatedBytesByTier"/>
    /// contributions. <paramref name="pageCacheBytes"/> sizes the read-path
    /// <see cref="PageResidencyTracker"/>; 0 disables it (no madvise / eviction queue).
    /// </summary>
    public BlobArenaManager(string basePath, long maxFileSize, PersistedSnapshotTier tier, long pageCacheBytes = DefaultBlobPageCacheBytes)
    {
        _basePath = basePath;
        _maxFileSize = maxFileSize;
        _tier = tier;
        Directory.CreateDirectory(basePath);

        _pageTracker = PageResidencyTracker.FromByteBudget(pageCacheBytes);
        // Per-tier static facts: metadata footprint and configured cap. ResidentBytes is
        // refreshed by _metricsTimer below; seed to 0 so the gauge appears immediately.
        Metrics.BlobPageTrackerResidentBytesByTier[_tier] = 0L;
        Metrics.BlobPageTrackerMetadataBytesByTier[_tier] = _pageTracker.MetadataBytes;
        Metrics.BlobPageTrackerMaxBytesByTier[_tier] =
            (long)_pageTracker.MaxCapacity * Environment.SystemPageSize;
        // Skip the timer + eviction ring + drain task entirely when the tracker is disabled
        // (MaxCapacity == 0): no residency to poll, no evictions to dispatch.
        if (_pageTracker.MaxCapacity > 0)
        {
            _metricsTimer = new Timer(RefreshResidencyMetric, null,
                dueTime: TimeSpan.FromSeconds(1), period: TimeSpan.FromSeconds(1));

            // Eviction queue sized at 10% of the tracker's slot capacity (rounded up to the next
            // power of two, floored at 64).
            int ringCapacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(64, _pageTracker.MaxCapacity / 10));
            _evictionRing = new MpmcRingBuffer<long>(ringCapacity);
            _evictionWake = new SemaphoreSlim(0, int.MaxValue);
            _evictionDrainCts = new CancellationTokenSource();
            _evictionDrainTask = Task.Run(() => DrainEvictionsAsync(_evictionDrainCts.Token));
        }
    }

    /// <summary>
    /// Rehydrate the file pool from on-disk files. Each <see cref="BlobArenaFile"/> restores
    /// its frontier from its own 8-byte header (the on-disk length is the pre-extended
    /// <see cref="BlobArenaFile.MaxSize"/> and no longer carries it). Must be called before any
    /// <see cref="PersistedSnapshots.PersistedSnapshot"/> is constructed so
    /// <see cref="TryLeaseFile"/> can resolve ids stored in their <c>ref_ids</c> metadata.
    /// </summary>
    public void Initialize()
    {
        lock (_lock)
        {
            foreach (string path in Directory.GetFiles(_basePath, $"*{BlobFileExtension}"))
            {
                string name = Path.GetFileName(path);
                if (!name.StartsWith(BlobFilePrefix, StringComparison.Ordinal)) continue;
                int id = ParseId(name);
                if (id < 0 || id > ushort.MaxValue) continue;
                long len = new FileInfo(path).Length;
                long maxSize = len > 0 ? Math.Max(len, _maxFileSize) : _maxFileSize;
                BlobArenaFile file = new(_tier, (ushort)id, path, maxSize);
                _files[id] = file;
                _nextFileId = Math.Max(_nextFileId, id + 1);
                if (file.Frontier < _maxFileSize) _mutableFiles.Add((ushort)id);
            }
        }
    }

    /// <summary>
    /// Open a writer that appends into an existing arena file with headroom (or a fresh
    /// one if none qualifies). The writer holds a lease on the underlying
    /// <see cref="BlobArenaFile"/> for its lifetime; <see cref="BlobArenaWriter.Dispose"/>
    /// drops it. The caller takes a separate snapshot lease via <see cref="TryLeaseFile"/>
    /// before disposing the writer.
    /// </summary>
    public BlobArenaWriter CreateWriter(long estimatedSize)
    {
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BlobArenaManager));

            ushort? chosen = null;
            List<ushort>? toRemove = null;
            foreach (ushort id in _mutableFiles)
            {
                BlobArenaFile candidate = _files[id]!;
                if (candidate.Frontier + estimatedSize <= candidate.MaxSize)
                {
                    chosen = id;
                    break;
                }
                (toRemove ??= []).Add(id);
            }
            if (toRemove is not null)
                foreach (ushort id in toRemove) _mutableFiles.Remove(id);

            ushort fileId;
            BlobArenaFile file;
            long startOffset;
            if (chosen is ushort existing)
            {
                fileId = existing;
                file = _files[fileId]!;
                startOffset = file.Frontier;
                // Reserve: remove from the mutable set so no concurrent CreateWriter picks it.
                // RegisterCompleted / CancelWrite re-add it if it still has headroom.
                _mutableFiles.Remove(fileId);
            }
            else
            {
                if (_nextFileId > ushort.MaxValue)
                    throw new InvalidOperationException(
                        $"Blob arena file id space exhausted ({ushort.MaxValue + 1} files).");
                fileId = (ushort)_nextFileId++;
                string path = Path.Combine(_basePath, $"{BlobFilePrefix}{fileId:D4}{BlobFileExtension}");
                // Fresh pre-extended file: the ctor seeds Frontier at HeaderSize, so the first
                // write lands past the frontier header.
                file = new BlobArenaFile(_tier, fileId, path, _maxFileSize);
                _files[fileId] = file;
                // Fresh file isn't added to _mutableFiles yet — Complete/Cancel adds it.
                startOffset = file.Frontier;
            }

            // The writer's lease keeps the file alive for the duration of the write. If
            // the file is mid-cleanup (shouldn't happen — we hold _lock), TryAcquireLease
            // returns false and we throw.
            if (!file.TryAcquireLease())
                throw new InvalidOperationException(
                    $"Blob arena {fileId} is mid-cleanup; cannot open writer.");

            FileStream stream = file.OpenWriteStream(startOffset);
            return new BlobArenaWriter(this, file, startOffset, stream);
        }
    }

    public bool TryLeaseFile(ushort blobArenaId, [NotNullWhen(true)] out BlobArenaFile? file)
    {
        // Lock-free: reference-slot reads are atomic and TryAcquireLease guards the race
        // where the file is mid-CleanUp (see the comment on _files). SweepUnreferenced/Dispose
        // either land before our read (slot is null) or after our lease (HasOnlyManagerLease
        // sees the extra lease and skips).
        BlobArenaFile? candidate = _files[blobArenaId];
        if (candidate is null || !candidate.TryAcquireLease())
        {
            file = null;
            return false;
        }
        file = candidate;
        return true;
    }

    public BlobArenaFile GetFile(ushort blobArenaId) =>
        _files[blobArenaId]
            ?? throw new InvalidOperationException(
                $"Blob arena {blobArenaId} not registered with this manager.");

    /// <summary>
    /// Record a single OS-page access by a reader of blob file <paramref name="blobArenaId"/>.
    /// Mirrors <see cref="ArenaReservation.TouchPage"/>: on a non-<see cref="TouchOutcome.Hit"/>
    /// outcome the page just entered the working set, so it is pre-faulted via
    /// <c>madvise(MADV_POPULATE_READ)</c>; on a displacement the evicted key is queued for an
    /// off-thread <c>madvise(MADV_DONTNEED)</c>. The caller (the hot read path) already holds a
    /// lease on the file, so its mapping stays valid for the duration of this call.
    /// </summary>
    public void TouchBlobPage(int blobArenaId, int pageIdx)
    {
        TouchOutcome outcome = _pageTracker.TryTouch(blobArenaId, pageIdx,
            out int evictedArenaId, out int evictedPageIdx);
        if (outcome == TouchOutcome.Hit) return;

        BlobArenaFile? file = _files[blobArenaId];
        file?.PopulateRead((long)pageIdx * Environment.SystemPageSize, Environment.SystemPageSize);

        if (outcome == TouchOutcome.Evicted)
            QueueEviction(evictedArenaId, evictedPageIdx);
    }

    /// <summary>
    /// Called by <see cref="BlobArenaWriter.Complete"/> after the writer has set the file's
    /// new frontier directly. The manager learns whether the id should be a packing
    /// candidate for the next writer and pushes the post-write frontier delta to
    /// <c>Metrics.BlobAllocatedBytesByTier</c>.
    /// </summary>
    internal void OnWriteCompleted(BlobArenaFile file, bool hasHeadroom)
    {
        lock (_lock)
        {
            if (hasHeadroom) _mutableFiles.Add(file.BlobArenaId);
            PushFrontierDelta(file);
        }
    }

    // Ratchet BlobAllocatedBytesByTier up to file.Frontier. Matches ArenaManager.PushFrontierDelta's
    // semantics: push the delta since the last report, bring ReportedFrontier in sync. Bytes are
    // **allocated** (Frontier), not mapped (MaxSize) — sparse-file zeros after the frontier are
    // excluded.
    private void PushFrontierDelta(BlobArenaFile file)
    {
        long current = file.Frontier;
        long reported = file.ReportedFrontier;
        long delta = current - reported;
        if (delta == 0) return;
        file.ReportedFrontier = current;
        Metrics.BlobAllocatedBytesByTier.AddOrUpdate(_tier,
            static (_, d) => d, static (_, b, d) => b + d, delta);
    }

    /// <summary>
    /// Called by <see cref="BlobArenaWriter.Dispose"/> on the cancel path. The writer's
    /// frontier didn't advance, so the file still has room by construction — re-add the
    /// id to the mutable pool. No file touch.
    /// </summary>
    internal void OnWriteCancelled(ushort blobArenaId)
    {
        lock (_lock) _mutableFiles.Add(blobArenaId);
    }

    /// <summary>
    /// Delete arena files that no snapshot referenced after a restart — recoverable
    /// orphans from a mid-write crash where Complete never ran (or where the owning
    /// snapshot was wiped before restart). Safe to call after every
    /// <see cref="PersistedSnapshots.PersistedSnapshotRepository.LoadFromCatalog"/>;
    /// no concurrent activity is expected at that point.
    /// </summary>
    public void SweepUnreferenced()
    {
        lock (_lock)
        {
            if (_disposed) return;
            for (int id = 0; id < _files.Length; id++)
            {
                BlobArenaFile? file = _files[id];
                if (file is null) continue;
                // File still has external lease(s) — a snapshot loaded it during LoadFromCatalog.
                if (!file.HasOnlyManagerLease) continue;
                _files[id] = null;
                _mutableFiles.Remove((ushort)id);
                // Drop the manager's array-slot lease. With no other lease holders the
                // file's refcount hits zero, CleanUp runs and deletes the on-disk file
                // (preserve flag isn't set — nothing called PersistOnShutdown on this).
                file.Dispose();
            }
        }
    }

    /// <inheritdoc/>
    public void TryResetOrphanedFrontier(BlobArenaFile file)
    {
        lock (_lock)
        {
            if (_disposed) return;
            // Slot may already have been replaced (Dispose nulls it out).
            if (_files[file.BlobArenaId] != file) return;
            // Re-check inside the lock — a racing TryLeaseFile or CreateWriter could
            // have bumped the refcount in the window between the caller's
            // HasOnlyManagerLease probe and us taking the lock.
            if (!file.HasOnlyManagerLease) return;
            // PersistedSnapshotRepository.Dispose flags every loaded blob with
            // PersistOnShutdown before disposing snapshots. The last snapshot's CleanUp
            // arrives here with HasOnlyManagerLease=true — without this guard we'd punch
            // a hole over the data range of a file the next session needs to rehydrate
            // intact (BlobArenaFile.CleanUp would keep the file on disk, but its bytes
            // would all read as zeros).
            if (file.IsShutdownPreserved) return;
            long prev = file.ReportedFrontier;
            if (prev <= BlobArenaFile.HeaderSize)
            {
                // Already empty; make sure it's a packing candidate and exit.
                _mutableFiles.Add(file.BlobArenaId);
                return;
            }

            // Take the file out of the packing pool BEFORE mutating Frontier. Strictly
            // redundant with _lock + the HasOnlyManagerLease re-check (CreateWriter also
            // takes _lock), but keeps the "files in _mutableFiles have a stable Frontier"
            // invariant locally obvious. Re-added at frontier=HeaderSize below.
            _mutableFiles.Remove(file.BlobArenaId);

            // Reclaim the orphaned data range [HeaderSize, prev) while still under _lock — a
            // racing CreateWriter would otherwise lease this file and append at HeaderSize, and
            // punching a range that now holds fresh data would corrupt it. Punch-hole frees the
            // disk blocks without changing the (pre-extended) file length, so the fixed mapping
            // stays valid; the page-cache + tracker entries for the range are dropped too.
            long size = prev - BlobArenaFile.HeaderSize;
            file.PunchHole(BlobArenaFile.HeaderSize, size);
            file.AdviseDontNeed(BlobArenaFile.HeaderSize, size);
            ForgetTrackerRange(file.BlobArenaId, BlobArenaFile.HeaderSize, size);

            file.Frontier = BlobArenaFile.HeaderSize;
            file.ReportedFrontier = BlobArenaFile.HeaderSize;
            // Persist the reset frontier durably so the next session restores an empty file.
            file.WriteFrontierHeader(BlobArenaFile.HeaderSize);
            file.Fsync();
            Metrics.BlobAllocatedBytesByTier.AddOrUpdate(_tier,
                static (_, _) => 0L, static (_, b, r) => Math.Max(0, b - r), prev - BlobArenaFile.HeaderSize);

            _mutableFiles.Add(file.BlobArenaId);
        }
    }

    // --- Eviction dispatch (duplicated from ArenaManager; keyed by blob file id) ---

    /// <summary>
    /// Drop tracker entries for every fully-covered OS page in <c>[byteOffset, byteOffset+byteSize)</c>.
    /// Mirrors <see cref="BlobArenaFile.AdviseDontNeed"/>'s page-rounding (offset rounded up, end
    /// rounded down). Runs outside the manager lock — the tracker is independent of file lifecycle.
    /// </summary>
    public void ForgetTrackerRange(int arenaId, long byteOffset, long byteSize)
    {
        if (_pageTracker.MaxCapacity == 0 || byteSize <= 0) return;
        int pageSize = Environment.SystemPageSize;
        long startPage = (byteOffset + pageSize - 1) / pageSize;
        long endPageExclusive = (byteOffset + byteSize) / pageSize;
        long pageCount = endPageExclusive - startPage;
        if (pageCount <= 0) return;
        for (long p = startPage; p < endPageExclusive; p++)
            _pageTracker.Forget(arenaId, (int)p);
        // Whole-range Forget is paired with a whole-range MADV_DONTNEED at the call sites —
        // the kernel has just dropped many pages at once, so refresh resident pages
        // proportionally so its LRU doesn't bleed into our working set. Same 1:2 ratio as
        // the single-page dispatch path.
        TouchWarmPages((int)Math.Min(int.MaxValue, pageCount * 2));
    }

    internal void QueueEviction(int arenaId, int pageIdx)
    {
        // Disabled tracker (no ring) — nothing to do; TryTouch always returns Hit, but stay
        // defensive for direct callers.
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

        // Ring full — fall back to inline dispatch so the eviction is not lost.
        Interlocked.Increment(ref _evictionsInlineFallback);
        Metrics.BlobPageTrackerEvictionsInlineFallbackByTier.AddOrUpdate(_tier, 1L, static (_, c) => c + 1);
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
        Metrics.BlobPageTrackerEvictionsDispatchedByTier.AddOrUpdate(_tier, 1L, static (_, c) => c + 1);
        DispatchEvictionInline(arenaId, pageIdx);
    }

    private void DispatchEvictionInline(int arenaId, int pageIdx)
    {
        BlobArenaFile? file = _files[arenaId];
        if (file is null) return;
        int pageSize = Environment.SystemPageSize;
        long offset = (long)pageIdx * pageSize;
        // madvise tolerates a stale/torn-down pointer (returns errno) so no lease is needed here;
        // TouchWarmPages below does a userspace load and leases the file itself.
        file.AdviseDontNeed(offset, pageSize);

        // 1:2 drop-to-warm ratio (one dropped page → two refreshed pages).
        TouchWarmPages(2);
    }

    // Refresh up to <paramref name="targetTouches"/> resident pages' kernel-side LRU position so
    // MADV_DONTNEED on a sibling doesn't pull them out of the page cache under memory pressure.
    private void TouchWarmPages(int targetTouches)
    {
        for (int i = 0; i < targetTouches; i++)
        {
            if (!_pageTracker.TryPickResidentPage(out int warmArenaId, out int warmPageIdx)) return;
            BlobArenaFile? warmFile = _files[warmArenaId];
            if (warmFile is null) continue;
            long warmOffset = (long)warmPageIdx * Environment.SystemPageSize;
            if (warmOffset >= warmFile.MaxSize) continue;
            // Userspace load on a torn-down mapping would SIGSEGV (madvise tolerates a bad
            // pointer; a raw load does not) — pin the file for the duration of the read.
            if (!warmFile.TryAcquireLease()) continue;
            try { warmFile.TouchByte(warmOffset); }
            finally { warmFile.Dispose(); }
        }
    }

    // Mirror the tracker's resident-bytes counter into the per-tier gauge. Runs on the ThreadPool
    // from a 1s System.Threading.Timer; ResidentBytes is a single Volatile.Read.
    private void RefreshResidencyMetric(object? _)
    {
        if (_disposed) return;
        Metrics.BlobPageTrackerResidentBytesByTier[_tier] = _pageTracker.ResidentBytes;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _metricsTimer?.Dispose();

        // Stop the drain task first so it doesn't race with file disposal below.
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
            for (int id = 0; id < _files.Length; id++)
            {
                BlobArenaFile? file = _files[id];
                if (file is null) continue;
                _files[id] = null;
                // Drop the manager's array-slot lease. If a snapshot still holds a lease,
                // the file's refcount stays positive; the snapshot's later Dispose triggers
                // CleanUp, which honours the PersistOnShutdown flag set by
                // PersistedSnapshotRepository.Dispose's first pass.
                file.Dispose();
            }
        }
        _pageTracker.Dispose();
        // Zero out per-tier gauges so a teardown doesn't leave stale entries behind.
        Metrics.BlobPageTrackerResidentBytesByTier[_tier] = 0L;
        Metrics.BlobPageTrackerMetadataBytesByTier[_tier] = 0L;
        Metrics.BlobPageTrackerMaxBytesByTier[_tier] = 0L;
    }

    private static int ParseId(string fileName)
    {
        string noExt = Path.GetFileNameWithoutExtension(fileName);
        if (!noExt.StartsWith(BlobFilePrefix, StringComparison.Ordinal)) return -1;
        return int.TryParse(noExt.AsSpan(BlobFilePrefix.Length), NumberStyles.None,
            CultureInfo.InvariantCulture, out int id) ? id : -1;
    }
}
