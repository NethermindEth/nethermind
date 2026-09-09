// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.State.Flat.Io;

namespace Nethermind.State.Flat.Test.Io;

/// <summary>
/// Test-only in-memory <see cref="IByteBufferWriter"/>: grows a native buffer and exposes the
/// written bytes via <see cref="WrittenSpan"/>. Production builds stream through the arena writer;
/// the tests use this to materialize a block/table in memory for assertions.
/// </summary>
internal sealed class PooledByteBufferWriter(int initialCapacity, long firstOffset = 0) : IDisposable
{
    private Writer _writer = new(initialCapacity, firstOffset);

    public ref Writer GetWriter() => ref _writer;
    public ReadOnlySpan<byte> WrittenSpan => _writer.WrittenSpan;

    /// <summary>Resets the write cursor to 0 without releasing the backing buffer.</summary>
    public void Reset() => _writer.Reset();

    public void Dispose() => _writer.ReturnBuffer();

    public unsafe struct Writer : IByteBufferWriter
    {
        private byte* _buffer;
        private int _capacity;
        private int _written;
        private readonly long _firstOffset;

        internal Writer(int initialCapacity, long firstOffset)
        {
            _capacity = initialCapacity;
            _buffer = initialCapacity == 0 ? null : (byte*)NativeMemory.Alloc((nuint)initialCapacity);
            _firstOffset = firstOffset;
        }

        public Span<byte> GetSpan(int sizeHint)
        {
            int remaining = _capacity - _written;
            if (sizeHint > remaining) Grow(sizeHint);
            return new Span<byte>(_buffer + _written, _capacity - _written);
        }

        public void Advance(int count) => _written += count;
        public readonly long Written => _written;
        public readonly long FirstOffset => _firstOffset;
        public readonly ReadOnlySpan<byte> WrittenSpan => new(_buffer, _written);

        /// <summary>Rewind the cursor to 0; keeps the backing buffer for reuse.</summary>
        public void Reset() => _written = 0;

        private void Grow(int sizeHint)
        {
            int needed = _written + sizeHint;
            int newSize = Math.Max(needed, _capacity == 0 ? 1 : _capacity * 2);

            byte* newBuffer = (byte*)NativeMemory.Alloc((nuint)newSize);
            if (_written > 0)
            {
                Buffer.MemoryCopy(_buffer, newBuffer, newSize, _written);
            }
            if (_buffer is not null) NativeMemory.Free(_buffer);
            _buffer = newBuffer;
            _capacity = newSize;
        }

        internal void ReturnBuffer()
        {
            byte* buffer = _buffer;
            _buffer = null;
            _capacity = 0;
            if (buffer is not null) NativeMemory.Free(buffer);
        }
    }
}
