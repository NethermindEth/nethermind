// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Manages multiple arena files for snapshot storage. Handles allocation,
/// reading, dead space tracking, and arena-level compaction.
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

    public IReadOnlyDictionary<int, long> DeadBytes => _deadBytes;

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
    /// Allocate space for data and write it to an arena file.
    /// </summary>
    public SnapshotLocation Allocate(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            ArenaFile arena = GetOrCreateArena(data.Length);
            long offset = _frontiers[arena.Id];
            arena.Write(offset, data);
            _frontiers[arena.Id] = offset + data.Length;
            return new SnapshotLocation(arena.Id, offset, data.Length);
        }
    }

    /// <summary>
    /// Reserve space in an arena file. The returned <see cref="ArenaReservation"/>
    /// holds a reference to this manager for zero-copy access via <see cref="ArenaReservation.GetSpan"/>.
    /// Caller must call either <see cref="ArenaReservation.FinalizedWrite"/> or <see cref="ArenaReservation.Return"/>.
    /// </summary>
    public ArenaReservation ReserveForWrite(int maximumSize)
    {
        lock (_lock)
        {
            ArenaFile file;
            if (maximumSize > DedicatedArenaThreshold)
            {
                file = CreateArenaFile(maximumSize, dedicated: true);
            }
            else
            {
                file = GetOrCreateArena(maximumSize);
            }

            long offset = _frontiers[file.Id];
            _frontiers[file.Id] = offset + maximumSize;
            _reservedArenas.Add(file.Id);
            return new ArenaReservation(this, file.Id, offset, maximumSize);
        }
    }

    /// <summary>
    /// Open an existing snapshot location as an <see cref="ArenaReservation"/> for zero-copy reads.
    /// </summary>
    public ArenaReservation Open(in SnapshotLocation location) =>
        new(this, location.ArenaId, location.Offset, location.Size);

    /// <summary>
    /// Get a span for the reservation's data region.
    /// </summary>
    public Span<byte> GetSpan(ArenaReservation reservation) =>
        _arenas[reservation.ArenaId].GetSpan(reservation.Offset, reservation.Size);

    /// <summary>
    /// Finalize a reservation. Data is already in the file via mmap; this adjusts bookkeeping.
    /// </summary>
    public SnapshotLocation FinalizedWrite(ArenaReservation reservation, int actualSize)
    {
        lock (_lock)
        {
            SnapshotLocation location = new(reservation.ArenaId, reservation.Offset, actualSize);
            _reservedArenas.Remove(reservation.ArenaId);
            int waste = reservation.MaxSize - actualSize;
            if (waste > 0)
            {
                if (_frontiers[reservation.ArenaId] == reservation.Offset + reservation.MaxSize)
                    _frontiers[reservation.ArenaId] = reservation.Offset + actualSize;
                else
                    _deadBytes[reservation.ArenaId] += waste;
            }
            reservation.Size = actualSize;
            return location;
        }
    }

    /// <summary>
    /// Cancel a reservation, rolling back the frontier or marking dead space.
    /// </summary>
    public void Return(ArenaReservation reservation)
    {
        lock (_lock)
        {
            _reservedArenas.Remove(reservation.ArenaId);

            if (_standaloneFiles.Contains(reservation.ArenaId))
            {
                _standaloneFiles.Remove(reservation.ArenaId);
                if (_arenas.Remove(reservation.ArenaId, out ArenaFile? file))
                {
                    file.Dispose();
                    File.Delete(file.Path);
                }
                _frontiers.Remove(reservation.ArenaId);
                _deadBytes.Remove(reservation.ArenaId);
            }
            else
            {
                if (_frontiers[reservation.ArenaId] == reservation.Offset + reservation.MaxSize)
                    _frontiers[reservation.ArenaId] = reservation.Offset;
                else
                    _deadBytes[reservation.ArenaId] += reservation.MaxSize;
            }
        }
    }

    /// <summary>
    /// Read snapshot data from an arena file. Used internally by Compact.
    /// </summary>
    private byte[] Read(in SnapshotLocation location) =>
        _arenas[location.ArenaId].Read(location.Offset, location.Size);

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

    /// <summary>
    /// Check if an arena should be compacted (>50% dead space).
    /// </summary>
    public bool ShouldCompact(int arenaId)
    {
        lock (_lock)
        {
            if (!_frontiers.TryGetValue(arenaId, out long frontier) || frontier == 0) return false;
            _deadBytes.TryGetValue(arenaId, out long dead);
            return dead > frontier / 2;
        }
    }

    /// <summary>
    /// Compact an arena by copying live entries to a new arena.
    /// Returns a mapping from old locations to new locations.
    /// The caller is responsible for updating the catalog.
    /// </summary>
    public Dictionary<SnapshotLocation, SnapshotLocation> Compact(int arenaId, IReadOnlyList<SnapshotLocation> liveLocations)
    {
        lock (_lock)
        {
            if (!_arenas.TryGetValue(arenaId, out ArenaFile? oldArena))
                return [];

            Dictionary<SnapshotLocation, SnapshotLocation> mapping = new(liveLocations.Count);
            if (liveLocations.Count == 0)
            {
                // No live data, just remove the arena
                _arenas.Remove(arenaId);
                _frontiers.Remove(arenaId);
                _deadBytes.Remove(arenaId);
                oldArena.Dispose();
                File.Delete(oldArena.Path);
                return mapping;
            }

            // Create new arena
            ArenaFile newArena = CreateArenaFile();

            // Copy live entries
            long newOffset = 0;
            foreach (SnapshotLocation oldLoc in liveLocations)
            {
                byte[] data = oldArena.Read(oldLoc.Offset, oldLoc.Size);
                newArena.Write(newOffset, data);
                SnapshotLocation newLoc = new(newArena.Id, newOffset, oldLoc.Size);
                mapping[oldLoc] = newLoc;
                newOffset += oldLoc.Size;
            }

            _frontiers[newArena.Id] = newOffset;
            _deadBytes[newArena.Id] = 0;

            // Remove old arena
            _arenas.Remove(arenaId);
            _frontiers.Remove(arenaId);
            _deadBytes.Remove(arenaId);
            oldArena.Dispose();
            File.Delete(oldArena.Path);

            return mapping;
        }
    }

    public IEnumerable<int> GetArenaIds()
    {
        lock (_lock)
        {
            return _arenas.Keys.ToArray();
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
            {
                arena.Dispose();
            }
            _arenas.Clear();
        }
    }
}
