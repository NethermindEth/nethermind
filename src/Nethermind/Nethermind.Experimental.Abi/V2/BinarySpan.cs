// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Experimental.Abi.V2;

public ref struct BinarySpanReader
{
    public ReadOnlySpan<byte> Span { get; }
    public int Position { get; set; }

    public BinarySpanReader(ReadOnlySpan<byte> span)
    {
        Span = span;
        Position = 0;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        if (Position + count > Span.Length) throw new ArgumentOutOfRangeException(nameof(count));
        var result = Span.Slice(Position, count);

        Position += count;
        return result;
    }

    public ReadOnlySpan<byte> ReadBytesPadded(int length)
    {
        ReadOnlySpan<byte> bytes = ReadBytes(length);
        var padding = Math.PadTo32(length) - length;
        Position += padding;

        return bytes;
    }
}

public ref struct BinarySpanWriter
{
    public readonly Span<byte> Span { get; }
    public int Position { get; set; }

    public BinarySpanWriter(Span<byte> span)
    {
        Span = span;
        Position = 0;
    }

    public void Write(scoped ReadOnlySpan<byte> bytes)
    {
        if (Position + bytes.Length > Span.Length) throw new ArgumentOutOfRangeException(nameof(bytes));
        bytes.CopyTo(Span.Slice(Position));
        Position += bytes.Length;
    }

    public void WritePadded(scoped ReadOnlySpan<byte> bytes)
    {
        Write(bytes);

        int padding = Math.PadTo32(bytes.Length) - bytes.Length;
        Position += padding;
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
