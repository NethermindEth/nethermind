// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.State.Flat.Hsst;

public interface IByteBufferWriter
{
    Span<byte> GetSpan(int sizeHint = 0);
    void Advance(int count);
    int Written { get; }
}

public unsafe struct SpanBufferWriter(Span<byte> buffer) : IByteBufferWriter
{
    private readonly byte* _buffer = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(buffer));
    private readonly int _length = buffer.Length;
    private int _written;

    public Span<byte> GetSpan(int sizeHint = 0) => new(_buffer + _written, _length - _written);
    public void Advance(int count) => _written += count;
    public int Written => _written;
}
