// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;

namespace Nethermind.BalRecorder;

/// <summary>
/// Stores arbitrary byte blobs indexed by block number, using an era file layout.
/// One file covers <see cref="EraSize"/> consecutive blocks. Each file starts with
/// a fixed 65536-byte header (8192 × 8 bytes: uint32 offset + uint32 size per slot).
/// Data records are appended after the header; offset 0 in a slot means no entry.
/// </summary>
public class EraFlatStore(string directory, string extension = "bin")
{
    private const int EraSize = 8192;
    private const int HeaderSize = EraSize * 8; // 65536 bytes

    private readonly ConcurrentDictionary<long, object> _eraLocks = new();

    private object EraLock(long blockNumber) =>
        _eraLocks.GetOrAdd(blockNumber / EraSize, static _ => new object());

    private string FilePath(long blockNumber) =>
        Path.Combine(directory, $"{blockNumber / EraSize:D8}.{extension}");

    public void Write(long blockNumber, ReadOnlySpan<byte> data)
    {
        Directory.CreateDirectory(directory);
        string path = FilePath(blockNumber);
        int slot = (int)(blockNumber % EraSize);

        lock (EraLock(blockNumber))
        {
            using FileStream fs = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

            if (fs.Length == 0)
                fs.Write(new byte[HeaderSize]);

            fs.Seek(0, SeekOrigin.End);
            uint offset = (uint)fs.Position;
            fs.Write(data);

            Span<byte> entry = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(entry, offset);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)data.Length);
            fs.Seek(slot * 8, SeekOrigin.Begin);
            fs.Write(entry);
        }
    }

    /// <summary>
    /// Reads the entry for <paramref name="blockNumber"/> and invokes <paramref name="action"/>
    /// with a span over the data. The span is only valid for the duration of the call.
    /// Returns <c>false</c> if no entry exists.
    /// </summary>
    public bool TryRead<TArg>(long blockNumber, ReadOnlySpanAction<byte, TArg> action, TArg arg)
    {
        string path = FilePath(blockNumber);
        if (!File.Exists(path)) return false;

        int slot = (int)(blockNumber % EraSize);

        lock (EraLock(blockNumber))
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            Span<byte> entry = stackalloc byte[8];
            fs.Seek(slot * 8, SeekOrigin.Begin);
            fs.ReadExactly(entry);

            uint offset = BinaryPrimitives.ReadUInt32BigEndian(entry);
            if (offset == 0) return false;

            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            byte[] rented = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                fs.Seek(offset, SeekOrigin.Begin);
                fs.ReadExactly(rented, 0, size);
                action(new ReadOnlySpan<byte>(rented, 0, size), arg);
                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
