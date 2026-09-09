// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Nethermind.Core;

namespace Nethermind.BalRecorder;

/// <summary>
/// Wraps a single era file: one <see cref="SafeFileHandle"/> kept open for the store's lifetime.
/// The 65536-byte slot-index header is cached in memory; reads use <c>RandomAccess</c> positioned I/O
/// (no locking required). Writes are serialized by a per-instance lock.
/// </summary>
public sealed class SlotFile : IDisposable
{
    public const int SlotsPerFile = 8192;
    private const int HeaderSize = SlotsPerFile * 8; // 65536 bytes

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

    public bool TryRead<TArg>(int slot, ReadOnlySpanAction<byte, TArg> action, TArg arg)
    {
        uint offset, size;
        lock (_writeLock)
        {
            ReadOnlySpan<byte> entry = _header.AsSpan(slot * 8, 8);
            offset = BinaryPrimitives.ReadUInt32BigEndian(entry);
            if (offset == 0) return false;
            size = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
        }
        if (size == 0 || size > 64 * MemorySizes.MiB) return false;

        byte[] rented = ArrayPool<byte>.Shared.Rent((int)size);
        try
        {
            RandomAccess.Read(_handle, rented.AsSpan(0, (int)size), offset);
            action(new ReadOnlySpan<byte>(rented, 0, (int)size), arg);
            return true;
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    public bool TryWrite(int slot, ReadOnlySpan<byte> data)
    {
        lock (_writeLock)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(slot * 8, 8)) != 0) return false;

            if (_length > uint.MaxValue)
                throw new InvalidOperationException($"Era file exceeded 4 GB limit at offset {_length}.");
            uint offset = (uint)_length;
            RandomAccess.Write(_handle, data, offset);
            _length += data.Length;

            Span<byte> entry = _header.AsSpan(slot * 8, 8);
            BinaryPrimitives.WriteUInt32BigEndian(entry, offset);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)data.Length);
            RandomAccess.Write(_handle, entry, slot * 8);
            return true;
        }
    }

    public void Dispose() => _handle.Dispose();
}
