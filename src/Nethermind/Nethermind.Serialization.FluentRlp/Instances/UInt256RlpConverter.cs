// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Int256;

namespace Nethermind.Serialization.FluentRlp.Instances;

/// <summary>Converts a <see cref="UInt256"/>.</summary>
public abstract class UInt256RlpConverter : IRlpLengthConverter<UInt256>
{
    private const int FixedLength = 32;

    /// <inheritdoc/>
    /// <exception cref="RlpReaderException">The item is not a canonical integer of at most 32 bytes.</exception>
    public static UInt256 Read(ref RlpReader reader)
    {
        ReadOnlySpan<byte> bigEndian = reader.ReadIntegerBytes();
        if (bigEndian.Length > FixedLength) throw new RlpReaderException("RLP integer is too large");
        return new UInt256(bigEndian, isBigEndian: true);
    }

    /// <inheritdoc/>
    public static void Write(ref RlpWriter writer, UInt256 value)
    {
        Span<byte> bigEndian = stackalloc byte[FixedLength];
        value.ToBigEndian(bigEndian);
        writer.WriteIntegerBytes(bigEndian);
    }

    /// <inheritdoc/>
    public static int GetLength(UInt256 value)
    {
        int byteCount = (value.BitLen + 7) / 8;
        return byteCount == 1 && value.u0 < 0x80 ? 1 : Rlp.LengthOfString(byteCount);
    }

    /// <summary>Reads a value encoded as a byte string of exactly 32 big-endian bytes, as for EIP-2930 storage keys.</summary>
    /// <exception cref="RlpReaderException">The item is not a byte string of 32 bytes.</exception>
    public static UInt256 ReadFixed(ref RlpReader reader)
    {
        ReadOnlySpan<byte> bytes = reader.ReadBytes(FixedLength);
        return new UInt256(
            BinaryPrimitives.ReadUInt64BigEndian(bytes[24..]),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[16..]),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt64BigEndian(bytes));
    }

    /// <summary>Writes <paramref name="value"/> as a byte string of exactly 32 big-endian bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteFixed(ref RlpWriter writer, UInt256 value)
    {
        // Writing through a stack buffer measured faster than writing the value in place.
        Span<byte> bytes = stackalloc byte[FixedLength];
        value.ToBigEndian(bytes);
        writer.Write(bytes);
    }
}

/// <summary>Extension methods for <see cref="UInt256RlpConverter"/>.</summary>
public static class UInt256RlpConverterExt
{
    /// <inheritdoc cref="UInt256RlpConverter.Read"/>
    public static UInt256 ReadUInt256(this ref RlpReader reader) => UInt256RlpConverter.Read(ref reader);

    /// <inheritdoc cref="UInt256RlpConverter.Write"/>
    public static void Write(this ref RlpWriter writer, UInt256 value) => UInt256RlpConverter.Write(ref writer, value);
}
