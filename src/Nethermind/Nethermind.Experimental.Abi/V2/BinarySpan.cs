// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Experimental.Abi.V2;

public ref struct BinarySpanReader
{
    private readonly ReadOnlySpan<byte> _span;
    private int _position;

    public BinarySpanReader(ReadOnlySpan<byte> span)
    {
        _span = span;
        _position = 0;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        if (_position + count > _span.Length) throw new ArgumentOutOfRangeException(nameof(count));
        var result = _span.Slice(_position, count);

        _position += count;
        return result;
    }

    public ReadOnlySpan<byte> ReadBytesPadded(int length)
    {
        ReadOnlySpan<byte> bytes = ReadBytes(length);
        var padding = Math.PadTo32(length) - length;
        _position += padding;

        return bytes;
    }
}

public ref struct BinarySpanWriter
{
    private readonly Span<byte> _span;
    private int _position;

    public int Written => _position;

    public BinarySpanWriter(Span<byte> span)
    {
        _span = span;
        _position = 0;
    }

    public void Write(scoped ReadOnlySpan<byte> bytes)
    {
        if (_position + bytes.Length > _span.Length) throw new ArgumentOutOfRangeException(nameof(bytes));
        bytes.CopyTo(_span.Slice(_position));
        _position += bytes.Length;
    }

    public void WritePadded(scoped ReadOnlySpan<byte> bytes)
    {
        Write(bytes);

        int padding = Math.PadTo32(bytes.Length) - bytes.Length;
        _position += padding;
    }
}


public static class Math
{
    // TODO: Use `UInt256` when dealing with lengths
    public static int PadTo32(int length)
    {
        int rem = length % 32;
        return rem == 0 ? length : length + (32 - rem);
    }
}
