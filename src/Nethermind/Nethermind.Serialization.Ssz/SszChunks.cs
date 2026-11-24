// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Int256;

namespace Nethermind.Serialization.Ssz;

/// <summary>Bytes in pooled arrays that are never moved, so a reserved span stays writable while later bytes are added.</summary>
/// <param name="chunkSize">The minimum size of each array, in bytes.</param>
internal sealed class SszChunks(int chunkSize) : IDisposable
{
    private readonly int _chunkSize = chunkSize > 0 ? chunkSize : throw new ArgumentOutOfRangeException(nameof(chunkSize));
    private Chunk? _first;
    private Chunk? _last;

    public int Length { get; private set; }

    /// <summary>Appends <paramref name="size"/> contiguous bytes, which the caller has to fill.</summary>
    /// <remarks>When the current array has no room for them, it ends where it is and a new array is started.</remarks>
    public Span<byte> Reserve(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        Length = checked(Length + size);
        Chunk chunk = _last is { } last && size <= last.Free ? last : AddChunk(Math.Max(size, _chunkSize));
        return chunk.Reserve(size);
    }

    /// <summary>Appends a copy of <paramref name="bytes"/>, which may be split between arrays.</summary>
    public void Write(scoped ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            int count = Math.Min(bytes.Length, _last is { Free: > 0 } last ? last.Free : _chunkSize);
            bytes[..count].CopyTo(Reserve(count));
            bytes = bytes[count..];
        }
    }

    /// <summary>The bytes, valid until more are added or this is disposed.</summary>
    public ReadOnlySequence<byte> ToSequence()
    {
        if (_first is null || _last is null) return ReadOnlySequence<byte>.Empty;
        _last.End();
        return new ReadOnlySequence<byte>(_first, 0, _last, _last.Memory.Length);
    }

    /// <summary>Hashes the bytes from <paramref name="start"/> to the end.</summary>
    public void Hash(int start, SszHasher hash, out UInt256 root)
    {
        if (_last is { } last && start >= last.RunningIndex)
        {
            hash(last.Used[(int)(start - last.RunningIndex)..], out root);
            return;
        }

        int length = Length - start;
        byte[] bytes = ArrayPool<byte>.Shared.Rent(length);
        ToSequence().Slice(start).CopyTo(bytes);
        hash(bytes.AsSpan(0, length), out root);
        ArrayPool<byte>.Shared.Return(bytes);
    }

    public void Dispose()
    {
        for (Chunk? chunk = _first; chunk is not null; chunk = chunk.NextChunk) chunk.Return();
        _first = _last = null;
        Length = 0;
    }

    private Chunk AddChunk(int size)
    {
        Chunk chunk = new(size);
        if (_last is null) _first = chunk;
        else _last.EndBefore(chunk);
        return _last = chunk;
    }

    private sealed class Chunk(int size) : ReadOnlySequenceSegment<byte>
    {
        private readonly byte[] _array = ArrayPool<byte>.Shared.Rent(size);
        private int _used;

        public int Free => _array.Length - _used;

        public ReadOnlySpan<byte> Used => _array.AsSpan(0, _used);

        public Chunk? NextChunk { get; private set; }

        public Span<byte> Reserve(int length)
        {
            Span<byte> reserved = _array.AsSpan(_used, length);
            _used += length;
            return reserved;
        }

        public void End() => Memory = _array.AsMemory(0, _used);

        public void EndBefore(Chunk next)
        {
            End();
            Next = NextChunk = next;
            next.RunningIndex = RunningIndex + _used;
        }

        public void Return() => ArrayPool<byte>.Shared.Return(_array);
    }
}
