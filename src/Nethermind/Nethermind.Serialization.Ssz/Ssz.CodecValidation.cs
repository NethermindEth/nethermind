// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.Serialization.Ssz;

public static partial class Ssz
{
    private const int SszOffsetSize = 4;

    /// <summary>Writes a four-byte SSZ offset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EncodeOffset(Span<byte> data, int offset) =>
        BinaryPrimitives.WriteInt32LittleEndian(data, offset);

    /// <summary>Reads a four-byte SSZ offset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DecodeOffset(ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadInt32LittleEndian(data);

    /// <summary>Throws an exception identifying malformed SSZ data.</summary>
    [DoesNotReturn]
    public static T ThrowInvalidData<T>(string typeName, string message) =>
        throw new InvalidDataException($"Invalid SSZ data for {typeName}: {message}");

    /// <summary>Throws an exception identifying malformed SSZ data.</summary>
    [DoesNotReturn]
    public static void ThrowInvalidData(string typeName, string message) =>
        throw new InvalidDataException($"Invalid SSZ data for {typeName}: {message}");

    /// <summary>Throws an exception identifying a value that violates its SSZ schema.</summary>
    [DoesNotReturn]
    public static void ThrowInvalidValue(string typeName, string fieldName, string message) =>
        throw new InvalidDataException($"Invalid SSZ value for {typeName}.{fieldName}: {message}");

    /// <summary>Validates the encoded length of a fixed-size SSZ value.</summary>
    public static void ValidateExactLength(int actualLength, int expectedLength, string typeName)
    {
        if (actualLength != expectedLength)
        {
            ThrowInvalidData(typeName, $"expected {expectedLength} bytes but found {actualLength}.");
        }
    }

    /// <summary>Validates the minimum encoded length of an SSZ value.</summary>
    public static void ValidateMinimumLength(int actualLength, int minimumLength, string typeName)
    {
        if (actualLength < minimumLength)
        {
            ThrowInvalidData(typeName, $"expected at least {minimumLength} bytes but found {actualLength}.");
        }
    }

    /// <summary>Validates the encoded length of a collection of fixed-size elements.</summary>
    public static void ValidateMultipleOf(int actualLength, int itemLength, string typeName)
    {
        if (actualLength % itemLength != 0)
        {
            ThrowInvalidData(typeName, $"expected a multiple of {itemLength} bytes but found {actualLength}.");
        }
    }

    /// <summary>Validates container offsets against the fixed section and input bounds.</summary>
    public static void ValidateDynamicOffsets(ReadOnlySpan<byte> data, int fixedLength, string typeName, ReadOnlySpan<int> offsets)
    {
        ValidateMinimumLength(data.Length, fixedLength, typeName);

        if (offsets.Length is 0)
        {
            return;
        }

        if (offsets[0] != fixedLength)
        {
            ThrowInvalidData(typeName, $"expected the first offset to be {fixedLength} but found {offsets[0]}.");
        }

        int previous = fixedLength;
        for (int index = 0; index < offsets.Length; index++)
        {
            int current = offsets[index];
            if (current < previous)
            {
                ThrowInvalidData(typeName, $"offset #{index} is out of order ({current} < {previous}).");
            }

            if (current > data.Length)
            {
                ThrowInvalidData(typeName, $"offset #{index} ({current}) exceeds the input length {data.Length}.");
            }

            previous = current;
        }
    }

    /// <summary>Validates the first collection offset and returns the element count.</summary>
    public static int ValidateVariableItemCount(ReadOnlySpan<byte> data, int firstOffset, string typeName)
    {
        ValidateMinimumLength(data.Length, SszOffsetSize, typeName);

        if (firstOffset < SszOffsetSize)
        {
            ThrowInvalidData(typeName, $"expected the first offset to be at least {SszOffsetSize} but found {firstOffset}.");
        }

        if (firstOffset > data.Length)
        {
            ThrowInvalidData(typeName, $"the first offset {firstOffset} exceeds the input length {data.Length}.");
        }

        if (firstOffset % SszOffsetSize != 0)
        {
            ThrowInvalidData(typeName, $"the first offset {firstOffset} is not aligned to {SszOffsetSize}-byte offsets.");
        }

        return firstOffset / SszOffsetSize;
    }

    /// <summary>Validates that the next collection offset is ordered and within the input.</summary>
    public static void ValidateNextOffset(ReadOnlySpan<byte> data, int currentOffset, int nextOffset, string typeName)
    {
        if (nextOffset < currentOffset)
        {
            ThrowInvalidData(typeName, $"offsets are out of order ({nextOffset} < {currentOffset}).");
        }

        if (nextOffset > data.Length)
        {
            ThrowInvalidData(typeName, $"offset {nextOffset} exceeds the input length {data.Length}.");
        }
    }

    /// <summary>Validates the element count of an SSZ vector.</summary>
    public static void ValidateVectorLength<T>(ReadOnlySpan<T> items, int expectedLength, string typeName, string fieldName)
    {
        if (items.Length != expectedLength)
        {
            ThrowInvalidValue(typeName, fieldName, $"expected {expectedLength} elements but found {items.Length}.");
        }
    }

    /// <summary>Validates the element limit of an SSZ list.</summary>
    public static void ValidateListLimit<T>(ReadOnlySpan<T> items, ulong limit, string typeName, string fieldName)
    {
        if ((ulong)items.Length > limit)
        {
            ThrowInvalidValue(typeName, fieldName, $"expected at most {limit} elements but found {items.Length}.");
        }
    }

    /// <summary>Copies a byte list after validating its element limit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] DecodeByteList(ReadOnlySpan<byte> data, ulong limit, string typeName, string fieldName)
    {
        ValidateListLimit(data, limit, typeName, fieldName);
        byte[] result = GC.AllocateUninitializedArray<byte>(data.Length);
        data.CopyTo(result);
        return result;
    }

    /// <summary>Validates that a bitvector is present and has the declared length.</summary>
    public static void ValidateBitvectorLength(BitArray? bits, int expectedLength, string typeName, string fieldName)
    {
        int actualLength = bits?.Length ?? 0;
        if (bits is null || actualLength != expectedLength)
        {
            ThrowInvalidValue(typeName, fieldName, $"expected {expectedLength} bits but found {actualLength}.");
        }
    }

    /// <summary>Validates the bit limit of an SSZ bitlist; null represents an empty list.</summary>
    public static void ValidateBitlistLimit(BitArray? bits, ulong limit, string typeName, string fieldName)
    {
        if (bits is not null && (ulong)bits.Length > limit)
        {
            ThrowInvalidValue(typeName, fieldName, $"expected at most {limit} bits but found {bits.Length}.");
        }
    }

    /// <summary>Decodes a bitlist after validating its bit limit.</summary>
    public static void DecodeBitList(ReadOnlySpan<byte> data, ulong limit, string typeName, out BitArray value)
    {
        ValidateMinimumLength(data.Length, 1, typeName);
        if (data[^1] != 0)
        {
            ulong bitLength = (ulong)(data.Length - 1) * 8 + (uint)BitOperations.Log2(data[^1]);
            if (bitLength > limit) ThrowInvalidData(typeName, $"bit length {bitLength} exceeds the SSZ limit {limit}.");
        }
        value = DecodeBitlist(data);
    }
}
