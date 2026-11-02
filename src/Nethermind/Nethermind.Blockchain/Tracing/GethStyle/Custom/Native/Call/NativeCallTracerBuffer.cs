// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core.Collections;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;

/// <summary>Holds the copied input and output bytes of every frame of one call trace.</summary>
/// <remarks>
/// A frame's bytes live as long as the trace, which a parallel block trace disposes on the thread that writes the
/// response rather than on the worker that traced it. One rental per frame then misses the shared pool on the worker
/// and overflows it on the writer, so the bytes are packed into a few chunks that the trace returns together.
/// A mutable struct: it is held in one field at a time and handed on by reference.
/// </remarks>
internal struct NativeCallTracerBuffer
{
    private const int MinChunkLength = 4 * 1024;
    // Below the large object heap, so a trace of many frames does not rent ever larger, half-empty arrays.
    private const int MaxChunkLength = 64 * 1024;

    private byte[]? _chunk;
    private int _used;
    // Doubles per chunk up to MaxChunkLength; an oversized copy gets a chunk of its own size and does not raise it.
    private int _chunkLength;
    private ArrayPoolList<byte[]>? _filledChunks;

    /// <summary>Copies <paramref name="data"/> into the buffer.</summary>
    /// <returns>The copy, valid until <see cref="Release"/>; empty data returns <c>default</c>.</returns>
    public ReadOnlyMemory<byte> Copy(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return default;

        byte[]? chunk = _chunk;
        if (chunk is null || chunk.Length - _used < data.Length)
        {
            chunk = Grow(data.Length);
        }

        Memory<byte> copy = chunk.AsMemory(_used, data.Length);
        data.CopyTo(copy.Span);
        _used += data.Length;
        return copy;
    }

    private byte[] Grow(int length)
    {
        if (_chunk is not null)
        {
            (_filledChunks ??= new ArrayPoolList<byte[]>(4)).Add(_chunk);
        }

        _chunkLength = _chunkLength == 0 ? MinChunkLength : Math.Min(2 * _chunkLength, MaxChunkLength);
        _chunk = ArrayPool<byte>.Shared.Rent(Math.Max(length, _chunkLength));
        _used = 0;
        return _chunk;
    }

    /// <summary>Returns every chunk to the pool, which invalidates all the copies made.</summary>
    public void Release()
    {
        if (_filledChunks is not null)
        {
            foreach (byte[] chunk in _filledChunks.AsSpan())
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }

            _filledChunks.Dispose();
            _filledChunks = null;
        }

        if (_chunk is not null)
        {
            ArrayPool<byte>.Shared.Return(_chunk);
            _chunk = null;
        }

        _used = 0;
        _chunkLength = 0;
    }
}
