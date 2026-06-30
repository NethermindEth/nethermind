// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Nethermind.StateDiffArchive.Storage;

/// <summary>
/// Wraps a single era file holding up to <see cref="SlotsPerFile"/> per-block blobs: one
/// <see cref="SafeFileHandle"/> kept open for the file's lifetime, with the slot-index header cached
/// in memory.
/// </summary>
/// <remarks>
/// Layout: a fixed <see cref="SlotsPerFile"/>×<see cref="SlotEntrySize"/>-byte header (per slot: 8-byte
/// big-endian offset, 4-byte big-endian size) followed by appended blobs. The 64-bit offset means a single
/// era file can hold the full 8192-block range regardless of per-block diff size (no 4 GB ceiling); a single
/// blob is still capped at <see cref="MaxBlobSize"/>. Reads use <c>RandomAccess</c> positioned I/O (no
/// locking required); writes are serialized by a per-instance lock. Overwriting an existing slot appends a
/// fresh blob and repoints the header; the superseded bytes become dead space (cheap, since reorgs over
/// recorded history are rare).
/// </remarks>
public sealed class SlotFile : IDisposable
{
    public const int SlotsPerFile = 8192;
    private const int SlotEntrySize = 12;                       // 8-byte offset + 4-byte size
    private const int HeaderSize = SlotsPerFile * SlotEntrySize; // 98304 bytes
    private const uint MaxBlobSize = 1u << 30;                    // 1 GiB per-block blob sanity cap

    private readonly SafeFileHandle _handle;
    private readonly byte[] _header = new byte[HeaderSize];
    private readonly Lock _writeLock = new();
    private long _length;

    public SlotFile(string path)
    {
        _handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        _length = RandomAccess.GetLength(_handle);
        if (_length == 0)
        {
            RandomAccess.Write(_handle, _header, 0);
            _length = HeaderSize;
        }
        else
        {
            RandomAccess.Read(_handle, _header, 0);
        }
    }

    public bool HasSlot(int slot)
    {
        lock (_writeLock)
        {
            return BinaryPrimitives.ReadUInt64BigEndian(_header.AsSpan(slot * SlotEntrySize, SlotEntrySize)) != 0;
        }
    }

    public bool TryRead<TArg>(int slot, ReadOnlySpanAction<byte, TArg> action, TArg arg)
    {
        ulong offset;
        uint size;
        lock (_writeLock)
        {
            ReadOnlySpan<byte> entry = _header.AsSpan(slot * SlotEntrySize, SlotEntrySize);
            offset = BinaryPrimitives.ReadUInt64BigEndian(entry);
            if (offset == 0) return false;
            size = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
        }
        if (size == 0 || size > MaxBlobSize) return false;

        byte[] rented = ArrayPool<byte>.Shared.Rent((int)size);
        try
        {
            RandomAccess.Read(_handle, rented.AsSpan(0, (int)size), (long)offset);
            action(new ReadOnlySpan<byte>(rented, 0, (int)size), arg);
            return true;
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    /// <param name="allowOverwrite">When false, a write to an already-filled slot is a no-op returning false.</param>
    /// <returns>True when the blob was written (or rewritten); false when the slot was occupied and overwrite was disallowed.</returns>
    public bool TryWrite(int slot, ReadOnlySpan<byte> data, bool allowOverwrite = false)
    {
        if (data.Length > MaxBlobSize)
            throw new InvalidOperationException($"State-diff blob of {data.Length} bytes exceeds the {MaxBlobSize}-byte per-block limit.");

        lock (_writeLock)
        {
            if (!allowOverwrite && BinaryPrimitives.ReadUInt64BigEndian(_header.AsSpan(slot * SlotEntrySize, SlotEntrySize)) != 0) return false;

            long offset = _length;
            RandomAccess.Write(_handle, data, offset);
            _length += data.Length;

            Span<byte> entry = _header.AsSpan(slot * SlotEntrySize, SlotEntrySize);
            BinaryPrimitives.WriteUInt64BigEndian(entry, (ulong)offset);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)data.Length);
            RandomAccess.Write(_handle, entry, slot * SlotEntrySize);
            return true;
        }
    }

    public void Dispose() => _handle.Dispose();
}
