// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;

namespace Nethermind.State.Flat.Hsst;

public sealed class PooledByteBufferWriter(int initialCapacity) : IDisposable
{
    private Writer _writer = new(ArrayPool<byte>.Shared.Rent(initialCapacity));

    public ref Writer GetWriter() => ref _writer;
    public ReadOnlySpan<byte> WrittenSpan => _writer.WrittenSpan;

    public void Dispose() => _writer.ReturnBuffer();

    public struct Writer : IByteBufferWriter
    {
        private byte[] _buffer;
        private int _written;

        internal Writer(byte[] buffer)
        {
            _buffer = buffer;
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            int remaining = _buffer.Length - _written;
            if (sizeHint > remaining)
                Grow(sizeHint);
            return _buffer.AsSpan(_written);
        }

        public void Advance(int count) => _written += count;
        public int Written => _written;
        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

        private void Grow(int sizeHint)
        {
            int needed = _written + sizeHint;
            int newSize = Math.Max(needed, _buffer.Length * 2);
            byte[] newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
            _buffer.AsSpan(0, _written).CopyTo(newBuffer);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = newBuffer;
        }

        internal void ReturnBuffer()
        {
            byte[] buffer = _buffer;
            _buffer = null!;
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
