// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Nethermind.State.Flat.Hsst;

public sealed class PooledByteBufferWriter(int initialCapacity, long firstOffset = 0) : IDisposable
{
    private Writer _writer = new(initialCapacity, firstOffset);

    public ref Writer GetWriter() => ref _writer;
    public ReadOnlySpan<byte> WrittenSpan => _writer.WrittenSpan;

    public void Dispose() => _writer.ReturnBuffer();

    public unsafe struct Writer : IByteBufferWriterWithReader<PooledByteBufferWriter.WriterReader, NoOpPin>
    {
        internal byte* _buffer;
        private int _capacity;
        private int _written;
        private readonly long _firstOffset;

        internal Writer(int initialCapacity, long firstOffset)
        {
            _capacity = initialCapacity;
            _buffer = initialCapacity == 0 ? null : (byte*)NativeMemory.Alloc((nuint)initialCapacity);
            _firstOffset = firstOffset;
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            int remaining = _capacity - _written;
            if (sizeHint > remaining) Grow(sizeHint);
            return new Span<byte>(_buffer + _written, _capacity - _written);
        }

        public void Advance(int count) => _written += count;
        public readonly long Written => _written;
        public readonly long FirstOffset => _firstOffset;
        public readonly ReadOnlySpan<byte> WrittenSpan => new(_buffer, _written);

        /// <summary>
        /// Reader covering [Written − pastSize, Written). The reader resolves the
        /// current backing pointer through <c>ref Writer</c> on every access, so a
        /// later <see cref="Grow"/> reallocation is safe between reads. Pins
        /// returned by <see cref="WriterReader.PinBuffer"/> however hold a span over
        /// the buffer at pin time and must not be held across writes that could
        /// trigger a grow.
        /// </summary>
        [UnscopedRef]
        public WriterReader OpenReader(long pastSize)
            => new(ref this, _written - checked((int)pastSize), checked((int)pastSize));

        public void DisposeActiveReader() { }

        private void Grow(int sizeHint)
        {
            int needed = _written + sizeHint;
            int newSize = Math.Max(needed, _capacity == 0 ? 1 : _capacity * 2);
            while (newSize < needed) newSize *= 2;

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

    /// <summary>
    /// Reader over a fixed window of a <see cref="Writer"/>. Holds a <c>ref</c> to
    /// the writer so the current backing pointer is resolved fresh on each access —
    /// safe across <see cref="Writer.GetSpan"/>-triggered reallocation.
    /// </summary>
    public readonly unsafe ref struct WriterReader : IHsstByteReader<NoOpPin>
    {
        private readonly ref Writer _writer;
        private readonly int _start;
        private readonly int _length;

        internal WriterReader(ref Writer writer, int start, int length)
        {
            _writer = ref writer;
            _start = start;
            _length = length;
        }

        public long Length => _length;

        public Bound Bound => new(0, _length);

        public bool TryRead(long offset, scoped Span<byte> output)
        {
            if ((ulong)offset > (ulong)(_length - output.Length)) return false;
            int from = _start + (int)offset;
            new ReadOnlySpan<byte>(_writer._buffer + from, output.Length).CopyTo(output);
            return true;
        }

        public NoOpPin PinBuffer(long offset, long size)
        {
            if ((ulong)offset + (ulong)size > (ulong)_length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            int from = _start + (int)offset;
            return new NoOpPin(new ReadOnlySpan<byte>(_writer._buffer + from, (int)size));
        }
    }
}
