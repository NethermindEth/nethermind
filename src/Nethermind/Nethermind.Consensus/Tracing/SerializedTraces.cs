// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text.Json;
using System.Threading;

namespace Nethermind.Consensus.Tracing;

/// <summary>The JSON of one transaction's traces, serialized by the worker that traced it and waiting for its turn in
/// the response. The bytes live in a chain of chunks, so a large trace never needs one contiguous buffer of its whole
/// size; the chunks come from the store of its <see cref="Pool"/>, and the instance with its JSON writer is reused.</summary>
internal sealed class SerializedTraces : IBufferWriter<byte>
{
    /// <summary>The size of a chunk. A chunk is larger only when the writer asks for one contiguous span that does not
    /// fit, as for a single long string value.</summary>
    internal const int ChunkSize = 16 * 1024;

    /// <summary>Larger chunks are allocated rather than rented, so that a rare oversized token is collected with its
    /// request instead of being kept in the shared array pool for the life of the process.</summary>
    internal const int MaxRentedChunkSize = 1024 * 1024;

    /// <summary>Chunk links an idle instance keeps; after a larger trace the rest are dropped, so the links of the
    /// largest trace ever seen are not kept by every instance.</summary>
    private const int KeptChunkLinks = 8;

    private readonly Pool _pool;
    private Chunk?[] _chunks = [];
    private int _chunkCount;
    private long _length;
    private Utf8JsonWriter? _writer;
    private JsonWriterOptions _options;

    private SerializedTraces(Pool pool) => _pool = pool;

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

        if (_chunkCount == _chunks.Length) Array.Resize(ref _chunks, Math.Max(KeptChunkLinks, 2 * _chunks.Length));
        Chunk next = _chunks[_chunkCount] ??= new Chunk();
        next.Start(_pool, Math.Max(ChunkSize, required));
        _chunkCount++;
        return next;
    }

    /// <summary>Gives every chunk back and empties the instance.</summary>
    private void Empty()
    {
        // A serialization that failed leaves bytes pending in the writer, over memory released below.
        _writer?.Reset();
        for (int i = 0; i < _chunkCount; i++) _chunks[i]!.Release(_pool);
        if (_chunks.Length > KeptChunkLinks) Array.Resize(ref _chunks, KeptChunkLinks);
        _chunkCount = 0;
        _length = 0;
    }

    /// <summary>One link of the chain: an array and how much of it is written.</summary>
    private sealed class Chunk : ReadOnlySequenceSegment<byte>
    {
        private byte[] _array = [];
        private int _written;

        public int Written => _written;
        public int Free => _array.Length - _written;
        public Memory<byte> FreeMemory => _array.AsMemory(_written);

        /// <summary>Makes the chunk empty with room for at least <paramref name="size"/> bytes.</summary>
        public void Start(Pool pool, int size)
        {
            _written = 0;
            _array = size == ChunkSize
                ? pool.RentChunk()
                : size <= MaxRentedChunkSize ? ArrayPool<byte>.Shared.Rent(size) : GC.AllocateUninitializedArray<byte>(size);
        }

        public void Advance(int count) => _written += count;

        public void Link(Chunk? next, long runningIndex)
        {
            Memory = _array.AsMemory(0, _written);
            Next = next;
            RunningIndex = runningIndex;
        }

        public void Release(Pool pool)
        {
            if (_array.Length == ChunkSize) pool.ReturnChunk(_array);
            else if (_array.Length is > 0 and <= MaxRentedChunkSize) ArrayPool<byte>.Shared.Return(_array);
            _array = [];
            _written = 0;
            Memory = default;
            Next = null;
        }
    }

    /// <summary>Instances and chunks kept for the next transactions. Every instance is kept, up to a count that covers
    /// the widest windows; chunks are kept in a store of their own up to a total of bytes, so the chunks of a backlog
    /// do not depend on what the shared array pool happens to keep, and memory kept after the requests stays capped.</summary>
    /// <param name="capacity">The idle instances kept.</param>
    /// <param name="retainedBytes">The bytes of idle chunks kept; more go back to the shared array pool.</param>
    internal sealed class Pool(int capacity, long retainedBytes)
    {
        private readonly Lock _lock = new();
        private readonly SerializedTraces?[] _idle = new SerializedTraces?[capacity];
        private int _count;
        private readonly Lock _chunkLock = new();
        private readonly byte[]?[] _chunks = new byte[]?[(int)(retainedBytes / ChunkSize)];
        private int _chunkCount;

        /// <summary>The chunks kept for reuse.</summary>
        internal int StoredChunks
        {
            get { lock (_chunkLock) return _chunkCount; }
        }

        /// <summary>An idle instance, preferably one whose writer already has <paramref name="options"/>.</summary>
        public SerializedTraces Rent(JsonWriterOptions options)
        {
            lock (_lock)
            {
                if (_count == 0) return new SerializedTraces(this);

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
                return serialized;
            }
        }

        public void Return(SerializedTraces serialized)
        {
            serialized.Empty();
            lock (_lock)
            {
                if (_count < _idle.Length)
                {
                    _idle[_count++] = serialized;
                    return;
                }
            }

            serialized._writer?.Dispose();
        }

        internal byte[] RentChunk()
        {
            lock (_chunkLock)
            {
                if (_chunkCount != 0)
                {
                    byte[] chunk = _chunks[--_chunkCount]!;
                    _chunks[_chunkCount] = null;
                    return chunk;
                }
            }

            return ArrayPool<byte>.Shared.Rent(ChunkSize);
        }

        internal void ReturnChunk(byte[] chunk)
        {
            lock (_chunkLock)
            {
                if (_chunkCount < _chunks.Length)
                {
                    _chunks[_chunkCount++] = chunk;
                    return;
                }
            }

            ArrayPool<byte>.Shared.Return(chunk);
        }
    }
}
