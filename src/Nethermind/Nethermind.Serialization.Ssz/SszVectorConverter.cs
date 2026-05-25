// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using Nethermind.Int256;

namespace Nethermind.Serialization.Ssz;

/// <summary>
/// Marker contract for fixed-length SSZ vector converters.
/// </summary>
/// <remarks>
/// Implementations must also expose a public constant <c>Length</c> member so
/// the SSZ source generator can calculate fixed offsets at generation time.
/// </remarks>
public interface SszVectorConverter<T>
{
    /// <summary>Decodes a value from its fixed-length SSZ byte vector representation.</summary>
    static abstract T FromSpan(ReadOnlySpan<byte> span);

    /// <summary>Encodes a value into its fixed-length SSZ byte vector representation.</summary>
    static abstract void ToSpan(Span<byte> span, T value);
}

public sealed class ByteSszVectorConverter : SszVectorConverter<byte>
{
    public const int Length = sizeof(byte);

    private ByteSszVectorConverter() { }

    public static byte FromSpan(ReadOnlySpan<byte> span) => Ssz.DecodeByte(span);

    public static void ToSpan(Span<byte> span, byte value) => Ssz.Encode(span, value);
}

public sealed class UInt16SszVectorConverter : SszVectorConverter<ushort>
{
    public const int Length = sizeof(ushort);

    private UInt16SszVectorConverter() { }

    public static ushort FromSpan(ReadOnlySpan<byte> span)
    {
        SszVectorConverterHelpers.ValidateLength(span, Length, nameof(UInt16SszVectorConverter));
        return BinaryPrimitives.ReadUInt16LittleEndian(span);
    }

    public static void ToSpan(Span<byte> span, ushort value) => Ssz.Encode(span, value);
}

public sealed class Int32SszVectorConverter : SszVectorConverter<int>
{
    public const int Length = sizeof(int);

    private Int32SszVectorConverter() { }

    public static int FromSpan(ReadOnlySpan<byte> span)
    {
        Ssz.Decode(span, out int result);
        return result;
    }

    public static void ToSpan(Span<byte> span, int value) => Ssz.Encode(span, value);
}

public sealed class UInt32SszVectorConverter : SszVectorConverter<uint>
{
    public const int Length = sizeof(uint);

    private UInt32SszVectorConverter() { }

    public static uint FromSpan(ReadOnlySpan<byte> span) => Ssz.DecodeUInt(span);

    public static void ToSpan(Span<byte> span, uint value) => Ssz.Encode(span, value);
}

public sealed class Int64SszVectorConverter : SszVectorConverter<long>
{
    public const int Length = sizeof(long);

    private Int64SszVectorConverter() { }

    public static long FromSpan(ReadOnlySpan<byte> span)
    {
        SszVectorConverterHelpers.ValidateLength(span, Length, nameof(Int64SszVectorConverter));
        return BinaryPrimitives.ReadInt64LittleEndian(span);
    }

    public static void ToSpan(Span<byte> span, long value) => Ssz.Encode(span, value);
}

public sealed class UInt64SszVectorConverter : SszVectorConverter<ulong>
{
    public const int Length = sizeof(ulong);

    private UInt64SszVectorConverter() { }

    public static ulong FromSpan(ReadOnlySpan<byte> span) => Ssz.DecodeULong(span);

    public static void ToSpan(Span<byte> span, ulong value) => Ssz.Encode(span, value);
}

public sealed class BooleanSszVectorConverter : SszVectorConverter<bool>
{
    public const int Length = sizeof(byte);

    private BooleanSszVectorConverter() { }

    public static bool FromSpan(ReadOnlySpan<byte> span)
    {
        SszVectorConverterHelpers.ValidateLength(span, Length, nameof(BooleanSszVectorConverter));
        return span[0] switch
        {
            0 => false,
            1 => true,
            byte value => throw new InvalidDataException($"SSZ bool must be 0 or 1, got {value}")
        };
    }

    public static void ToSpan(Span<byte> span, bool value) => Ssz.Encode(span, value);
}

public sealed class UInt256SszVectorConverter : SszVectorConverter<UInt256>
{
    public const int Length = 32;

    private UInt256SszVectorConverter() { }

    public static UInt256 FromSpan(ReadOnlySpan<byte> span) => Ssz.DecodeUInt256(span);

    public static void ToSpan(Span<byte> span, UInt256 value) => Ssz.Encode(span, value);
}

internal static class SszVectorConverterHelpers
{
    public static void ValidateLength(ReadOnlySpan<byte> span, int expectedLength, string converterName)
    {
        if (span.Length != expectedLength)
        {
            throw new InvalidDataException($"{converterName} expects input of length {expectedLength} and received {span.Length}");
        }
    }
}
