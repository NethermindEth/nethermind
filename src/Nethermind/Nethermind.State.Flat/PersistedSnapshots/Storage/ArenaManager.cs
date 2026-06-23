// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// Manages multiple arena files for snapshot storage. Handles allocation,
/// reading, and dead space tracking. Writes go through <see cref="ArenaWriter"/>
/// backed by FileStream; reads use mmap.
/// </summary>
public sealed class ArenaManager : IArenaManager
{
    private const string ArenaFilePrefix = "arena_";
    private const string SmallArenaFilePrefix = "small_arena_";
    private const string DedicatedArenaFilePrefix = "dedicated_";
    private const string ArenaFileExtension = ".bin";

    private readonly string _basePath;
    private readonly long _maxArenaSize;
    private readonly long _dedicatedArenaThreshold;
    private readonly bool _punchHoleOnReclaim;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<int, ArenaFile> _arenas = new();
    // Shared (non-dedicated) arenas with headroom AND not currently held by a writer. A writer
    // reserves a file by removing it from this set; its Complete / Cancel re-adds it if room
    // remains. Same pattern as BlobArenaManager.
    private readonly HashSet<int> _mutableArenas = [];
    // Same pool, but for sub-CompactSize (Small) arenas. Keeping the two tiers in disjoint files
    // segregates the cold, write-heavy small snapshots from the hot, long-lived large ones.
    private readonly HashSet<int> _mutableSmallArenas = [];
    private readonly Lock _lock = new();
    private int _nextArenaId;
    private bool _disposed;
    // 1 while fallocate(PUNCH_HOLE) is usable on the arena filesystem; latched to 0 the
    // first time the kernel reports it permanently unsupported.
    private int _punchHoleSupported = 1;

    public ArenaManager(string basePath, IFlatDbConfig config, ILogManager logManager)
    {
        _basePath = basePath;
        _maxArenaSize = config.ArenaFileSizeBytes;
        _dedicatedArenaThreshold = config.PersistedSnapshotDedicatedArenaThresholdBytes;
        _punchHoleOnReclaim = config.PersistedSnapshotPunchHoleOnReclaim;
        _logger = logManager.GetClassLogger<ArenaManager>();
        Directory.CreateDirectory(basePath);
    }

    /// <summary>
    /// Initialize from existing arena files and catalog entries.
    /// Computes allocation frontiers and dead bytes per arena.
    /// </summary>
    public void Initialize(IReadOnlyList<CatalogEntry> entries)
    {
        using Lock.Scope scope = _lock.EnterScope();
        // Open existing arena files. Defer the per-file metric push until after frontier
        // computation so the initial ArenaAllocatedBytes delta reflects the
        // catalog-derived high-water mark, not 0.
        foreach (string file in Directory.GetFiles(_basePath, $"*{ArenaFileExtension}"))
        {
            string fileName = Path.GetFileName(file);
            // Order matters: "small_arena_" does not start with "arena_", but check the longer/more
            // specific prefixes first to keep the classification unambiguous.
            string? prefix =
                fileName.StartsWith(DedicatedArenaFilePrefix, StringComparison.Ordinal) ? DedicatedArenaFilePrefix
                : fileName.StartsWith(SmallArenaFilePrefix, StringComparison.Ordinal) ? SmallArenaFilePrefix
                : fileName.StartsWith(ArenaFilePrefix, StringComparison.Ordinal) ? ArenaFilePrefix
                : null;
            if (prefix is null) continue;

            int arenaId = ParseArenaId(file, prefix);
            if (arenaId < 0) continue;

            long fileLength = new FileInfo(file).Length;
            long mappedSize = fileLength > 0 ? fileLength : _maxArenaSize;

            ArenaFile arena = new(arenaId, file, mappedSize, small: prefix == SmallArenaFilePrefix);
            _arenas[arenaId] = arena;
            _nextArenaId = Math.Max(_nextArenaId, arenaId + 1);
        }

        // Compute frontiers (max end-offset of any slice referencing the arena) and live
        // sizes from the catalog. Entries pointing at arena ids we didn't load on disk are
        // dropped — the catalog is the slower-moving authority but the on-disk file set is
        // what we can actually serve. The drop signals catalog/disk drift, so warn once per
        // missing arena id (not per entry).
        Dictionary<int, long> liveSizes = [];
        HashSet<int> missingArenas = [];
        foreach (CatalogEntry entry in entries)
        {
            int aid = entry.Location.ArenaId;
            if (!_arenas.TryGetValue(aid, out ArenaFile? arena))
            {
                if (missingArenas.Add(aid) && _logger.IsWarn)
                    _logger.Warn($"Persisted-snapshot catalog references arena {aid} with no on-disk file; dropping its entries.");
                continue;
            }
            long end = entry.Location.Offset + entry.Location.Size;
            if (end > arena.Frontier) arena.Frontier = end;

            liveSizes.TryGetValue(aid, out long live);
            liveSizes[aid] = live + entry.Location.Size;
        }

        // Now that frontiers reflect the catalog's high-water mark, push the per-file count + bytes
        // gauges in one go (seeds ReportedFrontier).
        foreach (KeyValuePair<int, ArenaFile> kv in _arenas)
        {
            liveSizes.TryGetValue(kv.Key, out long live);
            kv.Value.DeadBytes = kv.Value.Frontier - live;
            kv.Value.ReportAdded();
        }
    }

    /// <summary>
    /// Create an <see cref="ArenaWriter"/> for buffered writes. The arena is marked as
    /// reserved until the writer's <see cref="ArenaWriter.Complete"/> or
    /// <see cref="ArenaWriter.Dispose"/> fires. The writer owns the file ref for the
    /// duration of the write and signals back via <see cref="OnWriteCompleted"/> /
    /// <see cref="OnWriteCancelledShared"/> / <see cref="OnWriteCancelledDedicated"/>.
    /// </summary>
    public ArenaWriter CreateWriter(long estimatedSize, bool small = false)
    {
        using Lock.Scope scope = _lock.EnterScope();
        bool dedicated = estimatedSize >= _dedicatedArenaThreshold;
        ArenaFile file = dedicated
            ? CreateArenaFile(estimatedSize, dedicated: true, small: small)
            : GetOrCreateArena(estimatedSize, small);
        long offset = file.Frontier;
        // Reserve: remove from the mutable pool so no concurrent CreateWriter picks the same
        // file. OnWriteCompleted / OnWriteCancelledShared re-adds the id if room remains.
        // Dedicated files never enter the mutable pool. Route off file.Small (not the small
        // arg) so the remove always targets the same pool the file was scanned from.
        if (!dedicated) PoolFor(file).Remove(file.Id);
        FileStream stream = file.CreateWriteStream(offset);
        return new ArenaWriter(this, file, dedicated, offset, stream);
    }

    // The mutable pool a shared arena belongs to, chosen by its tier.
    private HashSet<int> PoolFor(ArenaFile file) => file.Small ? _mutableSmallArenas : _mutableArenas;

    /// <summary>
    /// Bookkeeping after <see cref="ArenaWriter.Complete"/>. The writer has already set
    /// <see cref="ArenaFile.Frontier"/> and (if dedicated) called <see cref="ArenaFile.Truncate"/>;
    /// the manager does NOT touch the file here. <paramref name="hasHeadroom"/> is true for
    /// shared writes whose post-frontier still leaves room for further packing.
    /// </summary>
    internal void OnWriteCompleted(ArenaFile file, bool hasHeadroom)
    {
        using Lock.Scope scope = _lock.EnterScope();
        if (hasHeadroom) PoolFor(file).Add(file.Id);
        // Ratchet ArenaAllocatedBytes up to file.Frontier (post-write high-water): push the
        // delta since the last report and bring file.ReportedFrontier in sync.
        long delta = file.Frontier - file.ReportedFrontier;
        if (delta != 0)
        {
            file.ReportedFrontier = file.Frontier;
            Interlocked.Add(ref Metrics._arenaAllocatedBytes, delta);
        }
    }

    /// <summary>
    /// Bookkeeping after a cancelled write on a shared (non-dedicated) arena: return the id
    /// to the mutable pool (the writer didn't advance the frontier, so by construction it
    /// still has the same headroom it had when picked).
    /// </summary>
    internal void OnWriteCancelledShared(ArenaFile file)
    {
        using Lock.Scope scope = _lock.EnterScope();
        PoolFor(file).Add(file.Id);
    }

    /// <summary>
    /// Bookkeeping after a cancelled write on a dedicated arena. The writer has already
    /// dropped the file's manager-ref (triggering <see cref="ArenaFile.CleanUp"/> →
    /// close + delete on disk); the manager just clears its dict / state and updates
    /// the byte metric. <paramref name="file"/> is readable post-dispose (Id /
    /// ReportedFrontier are plain fields).
    /// </summary>
    internal void OnWriteCancelledDedicated(ArenaFile file)
    {
        using Lock.Scope scope = _lock.EnterScope();
        _arenas.TryRemove(file.Id, out _);
        file.ReportRemoved();
    }

    /// <summary>
    /// Open an existing snapshot location as an <see cref="ArenaReservation"/> for zero-copy reads.
    /// Lookup is lock-free against the <see cref="ConcurrentDictionary{TKey,TValue}"/>; the race
    /// with a concurrent <see cref="MarkDead(ArenaFile, long)"/> tearing the file down is resolved
    /// by <see cref="ArenaFile.TryAcquireLease"/> inside the reservation's ctor — if the file has
    /// already started its CleanUp, the ctor surfaces an <see cref="InvalidOperationException"/>.
    /// </summary>
    public ArenaReservation Open(in SnapshotLocation location)
    {
        if (!_arenas.TryGetValue(location.ArenaId, out ArenaFile? arenaFile))
            throw new InvalidOperationException($"Arena {location.ArenaId} is not registered with this manager.");
        if (_logger.IsDebug) _logger.Debug($"Reserved arena {location.ArenaId} [{location.Offset}, {location.Offset + location.Size}) ({location.Size} bytes)");
        return new ArenaReservation(this, arenaFile, location.ArenaId, location.Offset, location.Size);
    }

    /// <summary>
    /// Mark <paramref name="deadSize"/> bytes of <paramref name="file"/> as dead and, if the
    /// file's dead-byte total has caught up with its frontier, drop the manager's dict ref so
    /// the file self-cleans once its last reservation releases its lease. The caller (typically
    /// <see cref="ArenaReservation.CleanUp"/>) already holds the file ref and handles file-side
    /// ops (<c>madvise</c> / <c>posix_fadvise</c>) itself — this method's sole job is the atomic
    /// set/dict/metric mutation that needs the manager lock.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the file survives in the manager; <c>false</c> if this call removed it
    /// (all bytes dead) or the manager is disposed.
    /// </returns>
    public bool MarkDead(ArenaFile file, long deadSize)
    {
        using Lock.Scope scope = _lock.EnterScope();
        // After Dispose, on-disk files must be preserved for the next session — skip
        // dead-byte accounting and file deletion entirely. Reporting "not surviving"
        // also makes ArenaReservation.CleanUp skip the hole punch, so a file the next
        // session rehydrates is never zeroed.
        if (_disposed) return false;
        // Sole caller is ArenaReservation.CleanUp, so one call == one reservation released.
        if (_logger.IsDebug) _logger.Debug($"Released arena reservation on arena {file.Id} ({deadSize} bytes)");
        file.DeadBytes += deadSize;
        if (file.DeadBytes < file.Frontier) return true;
        PoolFor(file).Remove(file.Id);
        if (_arenas.TryRemove(file.Id, out _))
        {
            if (_logger.IsDebug) _logger.Debug($"Released arena file {file.Id} (all {file.Frontier} bytes dead)");
            file.ReportRemoved();
            file.Dispose();
        }
        return false;
    }

    /// <inheritdoc/>
    public bool TryPunchHole(ArenaFile file, long offset, long size)
    {
        if (!_punchHoleOnReclaim || Volatile.Read(ref _punchHoleSupported) == 0) return false;
        PosixReclaim.PunchHoleOutcome outcome = file.PunchHole(offset, size);
        if (outcome == PosixReclaim.PunchHoleOutcome.Unsupported)
        {
            // First permanent "unsupported" from the kernel — stop trying on every later cleanup.
            Volatile.Write(ref _punchHoleSupported, 0);
        }
        return outcome == PosixReclaim.PunchHoleOutcome.Done;
    }

    /// <summary>
    /// Whether the adaptive punch-hole support flag is still set — i.e. no
    /// filesystem-unsupported error has been seen. Independent of the operator config flag.
    /// </summary>
    internal bool PunchHoleSupported => Volatile.Read(ref _punchHoleSupported) == 1;

    private ArenaFile GetOrCreateArena(long requiredSize, bool small)
    {
        // Scan the matching mutable pool (none currently held by a writer). Files that can't fit
        // are pruned (they become permanently read-only from the manager's POV).
        HashSet<int> pool = small ? _mutableSmallArenas : _mutableArenas;
        List<int>? toRemove = null;
        ArenaFile? result = null;
        foreach (int id in pool)
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
                pool.Remove(id);
        }

        return result ?? CreateArenaFile(small: small);
    }

    private ArenaFile CreateArenaFile(long mappedSize = 0, bool dedicated = false, bool small = false)
    {
        if (mappedSize == 0) mappedSize = _maxArenaSize;
        int id = _nextArenaId++;
        string prefix = dedicated ? DedicatedArenaFilePrefix : small ? SmallArenaFilePrefix : ArenaFilePrefix;
        string path = Path.Combine(_basePath, $"{prefix}{id:D4}{ArenaFileExtension}");
        ArenaFile arena = new(id, path, mappedSize, small);
        _arenas[id] = arena;
        if (_logger.IsDebug) _logger.Debug($"Created arena file {path} (mapped {mappedSize} bytes{(dedicated ? ", dedicated" : "")}{(small ? ", small" : "")})");
        // Fresh shared file isn't added to _mutableArenas — the writer that just took it
        // is its "owner". The writer's Complete / Cancel adds it (if room remains).
        arena.ReportAdded();
        return arena;
    }

    private static int ParseArenaId(string filePath, string prefix)
    {
        string fileName = Path.GetFileNameWithoutExtension(filePath);
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)) return -1;
        return int.TryParse(fileName.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int id) ? id : -1;
    }

    public void Dispose()
    {
        // Idempotent — owners higher up may also Dispose us through their own teardown.
        using Lock.Scope scope = _lock.EnterScope();
        if (_disposed) return;
        _disposed = true;
        foreach (KeyValuePair<int, ArenaFile> kv in _arenas)
        {
            kv.Value.ReportRemoved();
            kv.Value.Dispose();
        }
        _arenas.Clear();
    }
}
