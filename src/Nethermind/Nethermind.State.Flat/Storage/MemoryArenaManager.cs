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
    private int _nextArenaId;
    private readonly int _arenaSize;

    public MemoryArenaManager(int arenaSize = 64 * 1024)
    {
        _arenaSize = arenaSize;
    }

    public IReadOnlyDictionary<int, long> DeadBytes => _deadBytes;

    public void Initialize(IReadOnlyList<SnapshotCatalog.CatalogEntry> entries) { }

    public SnapshotLocation Allocate(ReadOnlySpan<byte> data)
    {
        int arenaId = GetOrCreateArena(data.Length);
        long offset = _frontiers[arenaId];
        data.CopyTo(_arenas[arenaId].AsSpan((int)offset));
        _frontiers[arenaId] = offset + data.Length;
        return new SnapshotLocation(arenaId, offset, data.Length);
    }

    public ArenaReservation ReserveForWrite(int maximumSize)
    {
        int arenaId = GetOrCreateArena(maximumSize);
        long offset = _frontiers[arenaId];
        _frontiers[arenaId] = offset + maximumSize;
        return new ArenaReservation(this, arenaId, offset, maximumSize);
    }

    public ArenaReservation Open(in SnapshotLocation location) =>
        new(this, location.ArenaId, location.Offset, location.Size);

    public Span<byte> GetSpan(ArenaReservation reservation) =>
        _arenas[reservation.ArenaId].AsSpan((int)reservation.Offset, reservation.Size);

    public SnapshotLocation FinalizedWrite(ArenaReservation reservation, int actualSize)
    {
        int waste = reservation.MaxSize - actualSize;
        if (waste > 0)
        {
            if (_frontiers[reservation.ArenaId] == reservation.Offset + reservation.MaxSize)
                _frontiers[reservation.ArenaId] = reservation.Offset + actualSize;
            else
            {
                _deadBytes.TryGetValue(reservation.ArenaId, out long dead);
                _deadBytes[reservation.ArenaId] = dead + waste;
            }
        }
        reservation.Size = actualSize;
        return new SnapshotLocation(reservation.ArenaId, reservation.Offset, actualSize);
    }

    public void Return(ArenaReservation reservation)
    {
        if (_frontiers[reservation.ArenaId] == reservation.Offset + reservation.MaxSize)
            _frontiers[reservation.ArenaId] = reservation.Offset;
        else
        {
            _deadBytes.TryGetValue(reservation.ArenaId, out long dead);
            _deadBytes[reservation.ArenaId] = dead + reservation.MaxSize;
        }
    }

    public void MarkDead(in SnapshotLocation location)
    {
        _deadBytes.TryGetValue(location.ArenaId, out long dead);
        _deadBytes[location.ArenaId] = dead + location.Size;
    }

    private int GetOrCreateArena(int requiredSize)
    {
        foreach (KeyValuePair<int, byte[]> kv in _arenas)
        {
            long frontier = _frontiers.GetValueOrDefault(kv.Key);
            if (frontier + requiredSize <= kv.Value.Length)
                return kv.Key;
        }

        int id = _nextArenaId++;
        int size = Math.Max(_arenaSize, requiredSize);
        _arenas[id] = new byte[size];
        _frontiers[id] = 0;
        _deadBytes[id] = 0;
        return id;
    }

    public void Dispose()
    {
        _arenas.Clear();
        _frontiers.Clear();
        _deadBytes.Clear();
    }
}
