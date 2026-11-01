// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text.Json;
using System.Threading;

namespace Nethermind.Consensus.Tracing;

/// <summary>The JSON of one transaction's traces, serialized by the worker that traced it and waiting for its turn in
/// the response. The bytes live in a chain of chunks rented from <see cref="ArrayPool{T}.Shared"/>, so a large trace
/// never needs one contiguous buffer of its whole size, and the instance with its chunks and its JSON writer is reused
/// from <see cref="Pool"/>.</summary>
internal sealed class SerializedTraces : IBufferWriter<byte>
{
    /// <summary>The size of a chunk. A chunk is larger only when the writer asks for one contiguous span that does not
    /// fit, as for a single long string value.</summary>
    internal const int ChunkSize = 16 * 1024;

    /// <summary>Larger chunks are allocated rather than rented, so that a rare oversized token is collected with its
    /// request instead of being kept in the shared array pool for the life of the process.</summary>
    internal const int MaxRentedChunkSize = 1024 * 1024;

    private Chunk?[] _chunks = [];
    private int _chunkCount;
    private int _length;
    private Utf8JsonWriter? _writer;
    private JsonWriterOptions _options;

    /// <summary>Bytes of chunk memory the instance keeps while idle in the pool.</summary>
    internal int RetainedBytes { get; private set; }

    /// <summary>The number of bytes written.</summary>
    public long Length => _length;

    /// <summary>The bytes written, one segment per chunk; valid until the instance is returned.</summary>
    public ReadOnlySequence<byte> Written
    {
        get
        {
            if (_length == 0) return ReadOnlySequence<byte>.Empty;

            Chunk last = _chunks[_chunkCount - 1]!;
            return new ReadOnlySequence<byte>(_chunks[0]!, 0, last, last.Written);
        }
    }

    /// <summary>Starts an empty serialization; the writer is replaced only when the options differ from its own.</summary>
    public Utf8JsonWriter Start(JsonWriterOptions options)
    {
        Utf8JsonWriter? writer = _writer;
        if (writer is null || !Matches(options))
        {
            writer?.Dispose();
            _options = options;
            return _writer = new Utf8JsonWriter(this, options);
        }

        writer.Reset();
        return writer;
    }

    /// <summary>Links the chunks into one sequence once the writer has flushed everything into them.</summary>
    public void Seal()
    {
        long runningIndex = 0;
        for (int i = 0; i < _chunkCount; i++)
        {
            Chunk chunk = _chunks[i]!;
            chunk.Link(i + 1 < _chunkCount ? _chunks[i + 1] : null, runningIndex);
            runningIndex += chunk.Written;
        }
    }

    internal bool Matches(JsonWriterOptions options) =>
        _writer is not null && ReferenceEquals(_options.Encoder, options.Encoder) && _options.MaxDepth == options.MaxDepth;

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0) return;

        Chunk current = _chunks[_chunkCount - 1]!;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, current.Free);
        current.Advance(count);
        _length += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0) => Current(sizeHint).FreeMemory;

    public Span<byte> GetSpan(int sizeHint = 0) => Current(sizeHint).FreeMemory.Span;

    private Chunk Current(int sizeHint)
    {
        int required = Math.Max(sizeHint, 1);
        if (_chunkCount != 0)
        {
            Chunk current = _chunks[_chunkCount - 1]!;
            if (current.Free >= required) return current;
        }

        if (_chunkCount == _chunks.Length) Array.Resize(ref _chunks, Math.Max(4, 2 * _chunks.Length));
        Chunk next = _chunks[_chunkCount] ??= new Chunk();
        next.Start(Math.Max(ChunkSize, required));
        _chunkCount++;
        return next;
    }

    /// <summary>Empties the instance; keeps its first chunk when <paramref name="retain"/> allows and the chunk has the
    /// regular size.</summary>
    /// <returns>The chunk bytes still held.</returns>
    private int Clear(bool retain)
    {
        // A serialization that failed leaves bytes pending in the writer, over memory released below.
        _writer?.Reset();
        int kept = 0;
        for (int i = 0; i < _chunks.Length; i++)
        {
            Chunk? chunk = _chunks[i];
            if (chunk is null) break;

            if (i == 0 && retain && chunk.Capacity == ChunkSize)
            {
                chunk.Rewind();
                kept = ChunkSize;
            }
            else
            {
                chunk.Release();
            }
        }

        _chunkCount = 0;
        _length = 0;
        return kept;
    }

    /// <summary>One link of the chain: a rented array and how much of it is written.</summary>
    private sealed class Chunk : ReadOnlySequenceSegment<byte>
    {
        private byte[] _array = [];
        private int _written;

        public int Capacity => _array.Length;
        public int Written => _written;
        public int Free => _array.Length - _written;
        public Memory<byte> FreeMemory => _array.AsMemory(_written);

        /// <summary>Makes the chunk empty with room for at least <paramref name="size"/> bytes.</summary>
        public void Start(int size)
        {
            _written = 0;
            if (_array.Length >= size) return;

            Release();
            _array = size <= MaxRentedChunkSize ? ArrayPool<byte>.Shared.Rent(size) : GC.AllocateUninitializedArray<byte>(size);
        }

        public void Advance(int count) => _written += count;

        public void Rewind() => _written = 0;

        public void Link(Chunk? next, long runningIndex)
        {
            Memory = _array.AsMemory(0, _written);
            Next = next;
            RunningIndex = runningIndex;
        }

        public void Release()
        {
            if (_array.Length is > 0 and <= MaxRentedChunkSize) ArrayPool<byte>.Shared.Return(_array);
            _array = [];
            _written = 0;
            Memory = default;
            Next = null;
        }
    }

    /// <summary>Instances kept for the next transactions. Every instance is kept, up to a count that covers the widest
    /// window, but only up to a total of retained chunk bytes; an instance kept past that keeps no chunk.</summary>
    internal sealed class Pool(int capacity, long retainedBytes)
    {
        private readonly Lock _lock = new();
        private readonly SerializedTraces?[] _idle = new SerializedTraces?[capacity];
        private int _count;
        private long _retained;

        /// <summary>An idle instance, preferably one whose writer already has <paramref name="options"/>.</summary>
        public SerializedTraces Rent(JsonWriterOptions options)
        {
            lock (_lock)
            {
                if (_count == 0) return new SerializedTraces();

                int pick = _count - 1;
                for (int i = _count - 1; i >= 0; i--)
                {
                    if (_idle[i]!.Matches(options))
                    {
                        pick = i;
                        break;
                    }
                }

                SerializedTraces serialized = _idle[pick]!;
                _idle[pick] = _idle[--_count];
                _idle[_count] = null;
                _retained -= serialized.RetainedBytes;
                return serialized;
            }
        }

        public void Return(SerializedTraces serialized)
        {
            lock (_lock)
            {
                bool keep = _count < _idle.Length;
                int kept = serialized.Clear(retain: keep && _retained + ChunkSize <= retainedBytes);
                serialized.RetainedBytes = kept;
                if (!keep)
                {
                    serialized._writer?.Dispose();
                    return;
                }

                _retained += kept;
                _idle[_count++] = serialized;
            }
        }
    }
}
