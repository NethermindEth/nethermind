// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;

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
    private const int DedicatedArenaThreshold = 512 * 1024 * 1024;

    private readonly string _basePath;
    private readonly long _maxArenaSize;
    private readonly Dictionary<int, ArenaFile> _arenas = [];
    private readonly Dictionary<int, long> _frontiers = [];
    private readonly Dictionary<int, long> _deadBytes = [];
    private readonly HashSet<int> _reservedArenas = [];
    private readonly HashSet<int> _standaloneFiles = [];
    private readonly object _lock = new();
    private int _nextArenaId;

    public ArenaManager(string basePath, long maxArenaSize = 4L * 1024 * 1024 * 1024)
    {
        _basePath = basePath;
        _maxArenaSize = maxArenaSize;
        Directory.CreateDirectory(basePath);
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
    public ArenaWriter CreateWriter(int estimatedSize)
    {
        lock (_lock)
        {
            ArenaFile file = estimatedSize >= DedicatedArenaThreshold
                ? CreateArenaFile(estimatedSize, dedicated: true)
                : GetOrCreateArena(estimatedSize);
            long offset = _frontiers[file.Id];
            _reservedArenas.Add(file.Id);
            ArenaFile.MmapWriteStream stream = file.CreateWriteStream(offset);
            return new ArenaWriter(this, file.Id, offset, stream);
        }
    }

    /// <summary>
    /// Complete a buffered write. Updates frontier and returns location + reservation.
    /// </summary>
    public (SnapshotLocation Location, ArenaReservation Reservation) CompleteWrite(int arenaId, long startOffset, int actualSize)
    {
        lock (_lock)
        {
            _frontiers[arenaId] = startOffset + actualSize;
            _reservedArenas.Remove(arenaId);
            SnapshotLocation location = new(arenaId, startOffset, actualSize);
            ArenaReservation reservation = new(this, arenaId, startOffset, actualSize);
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
                if (_arenas.Remove(arenaId, out ArenaFile? file))
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
    public ArenaReservation Open(in SnapshotLocation location) =>
        new(this, location.ArenaId, location.Offset, location.Size);

    /// <summary>
    /// Get a read-only span for the reservation's data region.
    /// </summary>
    public ReadOnlySpan<byte> GetSpan(ArenaReservation reservation) =>
        _arenas[reservation.ArenaId].GetSpan(reservation.Offset, reservation.Size);

    /// <summary>
    /// Mark space as dead for compaction tracking.
    /// </summary>
    public void MarkDead(in SnapshotLocation location)
    {
        lock (_lock)
        {
            _deadBytes.TryGetValue(location.ArenaId, out long dead);
            _deadBytes[location.ArenaId] = dead + location.Size;
        }
    }

    private ArenaFile GetOrCreateArena(int requiredSize)
    {
        foreach (KeyValuePair<int, ArenaFile> kv in _arenas)
        {
            if (_reservedArenas.Contains(kv.Key) || _standaloneFiles.Contains(kv.Key)) continue;
            long frontier = _frontiers.GetValueOrDefault(kv.Key);
            if (frontier + requiredSize <= kv.Value.MappedSize)
                return kv.Value;
        }

        return CreateArenaFile();
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
        lock (_lock)
        {
            foreach (ArenaFile arena in _arenas.Values)
                arena.Dispose();
            _arenas.Clear();
        }
    }
}
