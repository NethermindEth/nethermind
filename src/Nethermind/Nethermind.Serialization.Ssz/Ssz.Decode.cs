// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.IO;

namespace Nethermind.Serialization.Ssz;

public static partial class Ssz
{
    public static void Decode(ReadOnlySpan<byte> span, int vectorLength, out BitArray vector) =>
        vector = DecodeBitvector(span, vectorLength);

    public static void Decode(ReadOnlySpan<byte> span, out BitArray list) =>
        list = DecodeBitlist(span);

    public static BitArray DecodeBitvector(ReadOnlySpan<byte> span, int vectorLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vectorLength, nameof(vectorLength));

        int expectedBytes = (vectorLength + 7) / 8;
        if (span.Length != expectedBytes)
        {
            throw new InvalidDataException(
                $"Invalid bitvector: expected {expectedBytes} bytes for Bitvector[{vectorLength}] but got {span.Length}");
        }

        if (vectorLength % 8 != 0)
        {
            byte mask = (byte)(0xFF << (vectorLength % 8));
            if ((span[^1] & mask) != 0)
            {
                throw new InvalidDataException("Invalid bitvector: unused high bits are set");
            }
        }

        BitArray value = new(span.ToArray());
        value.Length = vectorLength;
        return value;
    }

    public static BitArray DecodeBitlist(ReadOnlySpan<byte> span)
    {
        if (span.Length == 0)
        {
            throw new InvalidDataException("Invalid bitlist: empty data (missing sentinel bit)");
        }

        if (span[^1] == 0)
        {
            throw new InvalidDataException("Invalid bitlist: last byte is zero (missing sentinel bit)");
        }

        BitArray value = new(span.ToArray());
        int length = value.Length - 1;
        int lastByte = span[^1];
        int mask = 0x80;
        while ((lastByte & mask) == 0 && mask > 0)
        {
            length--;
            mask >>= 1;
        }
        value.Length = length;
        return value;
    }

    public static void Decode(ReadOnlySequence<byte> data, int vectorLength, out BitArray vector)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vectorLength, nameof(vectorLength));

        int byteLength = (vectorLength + 7) / 8;
        if (data.Length != byteLength)
        {
            throw new InvalidDataException(
                $"Invalid bitvector: expected {byteLength} bytes for Bitvector[{vectorLength}] but got {data.Length}");
        }

        if (data.IsSingleSegment)
        {
            vector = DecodeBitvector(data.FirstSpan, vectorLength);
            return;
        }
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteLength);
        try
        {
            data.CopyTo(rented.AsSpan(0, byteLength));
            vector = DecodeBitvector(rented.AsSpan(0, byteLength), vectorLength);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static void Decode(ReadOnlySequence<byte> data, out BitArray list)
    {
        if (data.IsSingleSegment)
        {
            list = DecodeBitlist(data.FirstSpan);
            return;
        }
        int byteLength = (int)data.Length;
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteLength);
        try
        {
            data.CopyTo(rented.AsSpan(0, byteLength));
            list = DecodeBitlist(rented.AsSpan(0, byteLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

}
