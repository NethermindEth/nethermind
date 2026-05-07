// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// In-memory implementation of <see cref="IArenaManager"/> backed by byte arrays.
/// Intended for tests — no file I/O, no mmap.
/// </summary>
public sealed class MemoryArenaManager(int arenaSize = 64 * 1024) : IArenaManager
{
    private readonly Dictionary<int, byte[]> _arenas = [];
    // Each arena's byte[] is pinned via a GCHandle so GetReservationPointer can return
    // a stable raw pointer. Re-pinned on EnsureCapacity reallocation; freed on remove/Dispose.
    private readonly Dictionary<int, GCHandle> _arenaPins = [];
    private readonly Dictionary<int, long> _frontiers = [];
    private readonly Dictionary<int, long> _deadBytes = [];
    private readonly Dictionary<(int ArenaId, long Offset), MemoryStream> _pendingStreams = [];
    private readonly HashSet<int> _mutableArenas = [];
    private int _nextArenaId;
    private readonly int _arenaSize = arenaSize;

    public void Initialize(IReadOnlyList<SnapshotCatalog.CatalogEntry> entries) { }

    public ArenaWriter CreateWriter(long estimatedSize, string tag)
    {
        // Test-only: backed by byte[] so capped at int.MaxValue.
        int arenaId = GetOrCreateArena(checked((int)estimatedSize));
        long offset = _frontiers[arenaId];
        MemoryStream stream = new();
        _pendingStreams[(arenaId, offset)] = stream;
        return new ArenaWriter(this, arenaId, offset, stream, tag);
    }

    public (SnapshotLocation Location, ArenaReservation Reservation) CompleteWrite(int arenaId, long startOffset, long actualSize, string tag)
    {
        // Test-only: byte[]-backed arenas are int-bounded.
        int actualSizeInt = checked((int)actualSize);
        if (_pendingStreams.Remove((arenaId, startOffset), out MemoryStream? stream))
        {
            // Ensure arena has enough space
            EnsureCapacity(arenaId, checked((int)(startOffset + actualSize)));
            stream.GetBuffer().AsSpan(0, actualSizeInt).CopyTo(_arenas[arenaId].AsSpan(checked((int)startOffset)));
        }

        _frontiers[arenaId] = startOffset + actualSize;
        SnapshotLocation location = new(arenaId, startOffset, actualSize);
        ArenaReservation reservation = new(this, arenaId, startOffset, actualSize, tag);
        return (location, reservation);
    }

    public void CancelWrite(int arenaId, long startOffset) =>
        _pendingStreams.Remove((arenaId, startOffset));

    public ArenaReservation Open(in SnapshotLocation location, string tag) =>
        new(this, location.ArenaId, location.Offset, location.Size, tag);

    public ReadOnlySpan<byte> GetSpan(ArenaReservation reservation) =>
        _arenas[reservation.ArenaId].AsSpan(checked((int)reservation.Offset), checked((int)reservation.Size));

    public unsafe void GetReservationPointer(ArenaReservation reservation, out byte* dataPtr, out long size)
    {
        GCHandle pin = _arenaPins[reservation.ArenaId];
        dataPtr = (byte*)pin.AddrOfPinnedObject() + reservation.Offset;
        size = reservation.Size;
    }

    public IArenaWholeView OpenWholeView(ArenaReservation reservation) =>
        new MemoryWholeView(_arenas[reservation.ArenaId], checked((int)reservation.Offset), checked((int)reservation.Size));

    /// <summary>
    /// Find the still-pending writer for <paramref name="arenaId"/> whose key range
    /// covers <paramref name="absoluteOffset"/> and return a view borrowing its
    /// <see cref="MemoryStream.GetBuffer"/>. The pending stream remains owned by this
    /// manager — view disposal only releases the GCHandle pin, not the buffer.
    /// </summary>
    public IArenaWholeView OpenPendingView(int arenaId, long absoluteOffset, long size)
    {
        foreach (KeyValuePair<(int ArenaId, long Offset), MemoryStream> kv in _pendingStreams)
        {
            if (kv.Key.ArenaId != arenaId) continue;
            long streamStart = kv.Key.Offset;
            long streamEnd = streamStart + kv.Value.Length;
            if (absoluteOffset < streamStart || absoluteOffset + size > streamEnd) continue;
            byte[] buf = kv.Value.GetBuffer();
            int relOffset = checked((int)(absoluteOffset - streamStart));
            return new MemoryWholeView(buf, relOffset, checked((int)size));
        }
        throw new InvalidOperationException(
            $"No pending writer for arena {arenaId} covers absolute range [{absoluteOffset}, {absoluteOffset + size}).");
    }

    private sealed unsafe class MemoryWholeView : IArenaWholeView
    {
        private readonly byte[] _buffer;
        private readonly int _offset;
        private GCHandle _handle;
        public byte* DataPtr { get; }
        public long Size { get; }

        public MemoryWholeView(byte[] buffer, int offset, int size)
        {
            _buffer = buffer;
            _offset = offset;
            Size = size;
            _handle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
            DataPtr = (byte*)_handle.AddrOfPinnedObject() + offset;
        }

        public ReadOnlySpan<byte> GetSpan() => _buffer.AsSpan(_offset, checked((int)Size));
        public void Dispose() { if (_handle.IsAllocated) _handle.Free(); }
    }

    public void AdviseDontNeed(ArenaReservation reservation) { }

    public void Touch(ArenaReservation reservation, long subOffset, long size) { }

    public void TouchPage(int arenaId, int pageIdx) { }

    public PageResidencyTracker PageTracker { get; } = new(0);

    public int ArenaFileCount => _arenas.Count;

    public long ArenaMappedBytes
    {
        get
        {
            long sum = 0;
            foreach (byte[] arena in _arenas.Values) sum += arena.Length;
            return sum;
        }
    }

    public void MarkDead(in SnapshotLocation location)
    {
        _deadBytes.TryGetValue(location.ArenaId, out long dead);
        long totalDead = dead + location.Size;
        _deadBytes[location.ArenaId] = totalDead;

        if (totalDead >= _frontiers[location.ArenaId])
        {
            _mutableArenas.Remove(location.ArenaId);
            _arenas.Remove(location.ArenaId);
            if (_arenaPins.Remove(location.ArenaId, out GCHandle pin) && pin.IsAllocated) pin.Free();
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
            arena?.AsSpan(0, Math.Min(arena.Length, newSize)).CopyTo(newArena);
            // Re-pin to keep the raw pointer stable for the lifetime of the new buffer.
            if (_arenaPins.Remove(arenaId, out GCHandle oldPin)) oldPin.Free();
            _arenaPins[arenaId] = GCHandle.Alloc(newArena, GCHandleType.Pinned);
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
        byte[] arena = new byte[size];
        _arenas[newId] = arena;
        _arenaPins[newId] = GCHandle.Alloc(arena, GCHandleType.Pinned);
        _frontiers[newId] = 0;
        _deadBytes[newId] = 0;
        _mutableArenas.Add(newId);
        return newId;
    }

    public void Dispose()
    {
        foreach (GCHandle pin in _arenaPins.Values)
            if (pin.IsAllocated) pin.Free();
        _arenaPins.Clear();
        _arenas.Clear();
        _frontiers.Clear();
        _deadBytes.Clear();
        _pendingStreams.Clear();
        _mutableArenas.Clear();
        PageTracker.Dispose();
    }
}
