// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.State.Flat.Hsst;

public sealed class PooledByteBufferWriter(int initialCapacity) : IDisposable
{
    private Writer _writer = new(initialCapacity);

    public ref Writer GetWriter() => ref _writer;
    public ReadOnlySpan<byte> WrittenSpan => _writer.WrittenSpan;

    public void Dispose() => _writer.ReturnBuffer();

    public unsafe struct Writer : IByteBufferWriter
    {
        private byte* _buffer;
        private int _capacity;
        private int _written;

        internal Writer(int initialCapacity)
        {
            _capacity = initialCapacity;
            _buffer = initialCapacity == 0 ? null : (byte*)NativeMemory.Alloc((nuint)initialCapacity);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            int remaining = _capacity - _written;
            if (sizeHint > remaining) Grow(sizeHint);
            return new Span<byte>(_buffer + _written, _capacity - _written);
        }

        public void Advance(int count) => _written += count;
        public readonly long Written => _written;
        public readonly ReadOnlySpan<byte> WrittenSpan => new(_buffer, _written);

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
}
