// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;

namespace Nethermind.Consensus.Tracing;

/// <summary>The JSON of one transaction's traces, serialized by the worker that traced it and waiting for its turn in
/// the response. The bytes live in an array rented from <see cref="ArrayPool{T}.Shared"/>, and the instance with its
/// JSON writer is reused from <see cref="Pool"/>.</summary>
internal sealed class SerializedTraces : IBufferWriter<byte>
{
    private const int InitialBufferSize = 4096;

    /// <summary>A buffer up to this size stays with the instance between transactions; a larger one goes back to the
    /// shared array pool, so one large trace does not pin its memory to an idle instance.</summary>
    private const int RetainedBufferSize = 16 * 1024;

    private byte[] _buffer = [];
    private int _written;
    private Utf8JsonWriter? _writer;
    private JsonWriterOptions _options;

    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _written);

    /// <summary>Starts an empty serialization; the options decide the writer only when they differ from the last use.</summary>
    public Utf8JsonWriter Start(JsonWriterOptions options)
    {
        Utf8JsonWriter? writer = _writer;
        if (writer is null || !ReferenceEquals(_options.Encoder, options.Encoder) || _options.MaxDepth != options.MaxDepth)
        {
            writer?.Dispose();
            _options = options;
            return _writer = new Utf8JsonWriter(this, options);
        }

        writer.Reset();
        return writer;
    }

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _buffer.Length - _written);
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    private void EnsureCapacity(int sizeHint)
    {
        int required = Math.Max(sizeHint, 1);
        if (_buffer.Length - _written >= required) return;

        int size = Math.Max(Math.Max(InitialBufferSize, _written + required), 2 * _buffer.Length);
        byte[] grown = ArrayPool<byte>.Shared.Rent(size);
        _buffer.AsSpan(0, _written).CopyTo(grown);
        if (_buffer.Length != 0) ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = grown;
    }

    private void Clear()
    {
        // A serialization that failed leaves bytes pending in the writer, over memory this may hand back below.
        _writer?.Reset();
        _written = 0;
        if (_buffer.Length <= RetainedBufferSize) return;

        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }

    /// <summary>Instances kept for the next transactions, up to a fixed number; one beyond it is dropped with its buffer
    /// returned.</summary>
    internal sealed class Pool(int capacity)
    {
        private readonly ConcurrentQueue<SerializedTraces> _idle = new();
        private int _count;

        public SerializedTraces Rent()
        {
            if (!_idle.TryDequeue(out SerializedTraces? serialized)) return new SerializedTraces();

            Interlocked.Decrement(ref _count);
            return serialized;
        }

        public void Return(SerializedTraces serialized)
        {
            serialized.Clear();
            if (Interlocked.Increment(ref _count) <= capacity)
            {
                _idle.Enqueue(serialized);
                return;
            }

            Interlocked.Decrement(ref _count);
            serialized._writer?.Dispose();
            if (serialized._buffer.Length != 0) ArrayPool<byte>.Shared.Return(serialized._buffer);
            serialized._buffer = [];
        }
    }
}
