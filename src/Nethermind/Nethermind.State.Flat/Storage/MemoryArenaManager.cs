// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// In-memory implementation of <see cref="IArenaManager"/> backed by byte arrays.
/// Intended for tests — no file I/O, no mmap.
/// </summary>
public sealed class MemoryArenaManager : IArenaManager
{
    private readonly Dictionary<int, byte[]> _arenas = [];
    private readonly Dictionary<int, long> _frontiers = [];
    private readonly Dictionary<int, long> _deadBytes = [];
    private readonly Dictionary<(int ArenaId, long Offset), MemoryStream> _pendingStreams = [];
    private readonly HashSet<int> _mutableArenas = [];
    private int _nextArenaId;
    private readonly int _arenaSize;

    public MemoryArenaManager(int arenaSize = 64 * 1024)
    {
        _arenaSize = arenaSize;
    }

    public void Initialize(IReadOnlyList<SnapshotCatalog.CatalogEntry> entries) { }

    public ArenaWriter CreateWriter(int estimatedSize)
    {
        int arenaId = GetOrCreateArena(estimatedSize);
        long offset = _frontiers[arenaId];
        MemoryStream stream = new();
        _pendingStreams[(arenaId, offset)] = stream;
        return new ArenaWriter(this, arenaId, offset, stream);
    }

    public (SnapshotLocation Location, ArenaReservation Reservation) CompleteWrite(int arenaId, long startOffset, int actualSize)
    {
        if (_pendingStreams.Remove((arenaId, startOffset), out MemoryStream? stream))
        {
            // Ensure arena has enough space
            EnsureCapacity(arenaId, (int)(startOffset + actualSize));
            stream.GetBuffer().AsSpan(0, actualSize).CopyTo(_arenas[arenaId].AsSpan((int)startOffset));
        }

        _frontiers[arenaId] = startOffset + actualSize;
        SnapshotLocation location = new(arenaId, startOffset, actualSize);
        ArenaReservation reservation = new(this, arenaId, startOffset, actualSize);
        return (location, reservation);
    }

    public void CancelWrite(int arenaId, long startOffset) =>
        _pendingStreams.Remove((arenaId, startOffset));

    public ArenaReservation Open(in SnapshotLocation location) =>
        new(this, location.ArenaId, location.Offset, location.Size);

    public ReadOnlySpan<byte> GetSpan(ArenaReservation reservation) =>
        _arenas[reservation.ArenaId].AsSpan((int)reservation.Offset, reservation.Size);

    public void MarkDead(in SnapshotLocation location)
    {
        _deadBytes.TryGetValue(location.ArenaId, out long dead);
        long totalDead = dead + location.Size;
        _deadBytes[location.ArenaId] = totalDead;

        if (totalDead >= _frontiers[location.ArenaId])
        {
            _mutableArenas.Remove(location.ArenaId);
            _arenas.Remove(location.ArenaId);
            _frontiers.Remove(location.ArenaId);
            _deadBytes.Remove(location.ArenaId);
        }
    }

    private void EnsureCapacity(int arenaId, int needed)
    {
        if (!_arenas.TryGetValue(arenaId, out byte[]? arena) || needed > arena.Length)
        {
            int newSize = Math.Max(_arenaSize, needed);
            byte[] newArena = new byte[newSize];
            if (arena is not null)
                arena.AsSpan(0, Math.Min(arena.Length, newSize)).CopyTo(newArena);
            _arenas[arenaId] = newArena;
        }
    }

    private int GetOrCreateArena(int requiredSize)
    {
        // Scan only mutable arenas; remove any that can't fit (they become permanently read-only)
        List<int>? toRemove = null;
        int result = -1;
        foreach (int id in _mutableArenas)
        {
            long frontier = _frontiers.GetValueOrDefault(id);
            if (frontier + requiredSize <= _arenas[id].Length)
            {
                result = id;
                break;
            }

            (toRemove ??= []).Add(id);
        }

        if (toRemove is not null)
        {
            foreach (int id in toRemove)
                _mutableArenas.Remove(id);
        }

        if (result >= 0) return result;

        int newId = _nextArenaId++;
        int size = Math.Max(_arenaSize, requiredSize);
        _arenas[newId] = new byte[size];
        _frontiers[newId] = 0;
        _deadBytes[newId] = 0;
        _mutableArenas.Add(newId);
        return newId;
    }

    public void Dispose()
    {
        _arenas.Clear();
        _frontiers.Clear();
        _deadBytes.Clear();
        _pendingStreams.Clear();
        _mutableArenas.Clear();
    }
}
