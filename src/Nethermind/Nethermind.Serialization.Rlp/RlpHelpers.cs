// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// Shared helper methods for RLP processing to avoid code duplication between 
/// ValueRlpStream, RlpStream, and ValueDecoderContext.
/// </summary>
internal static class RlpHelpers
{
    public const int SmallPrefixBarrier = 56;

    private static readonly sbyte[] _prefixLengthWithLookupTable = BuildPrefixLenTable();
    private static readonly sbyte[] _contentLengthTable = BuildContentLenTable();
    private static readonly byte[] _prefixLengthTable = BuildPrefixLengthTable();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPrefixLength(byte prefixByte)
    {
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_prefixLengthTable), prefixByte);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static sbyte GetPrefixLengthWithLookup(int prefixByte)
    {
        Debug.Assert((uint)prefixByte <= byte.MaxValue);
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_prefixLengthWithLookupTable), prefixByte);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static sbyte GetContentLength(int prefixByte)
    {
        Debug.Assert((uint)prefixByte <= byte.MaxValue);
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_contentLengthTable), prefixByte);
    }

    private static byte[] BuildPrefixLengthTable()
    {
        byte[] table = new byte[byte.MaxValue];
        for (int i = 0; i < table.Length; i++)
        {
            if (i < 0x80)
                table[i] = 0; // single byte
            else if (i <= 0xB7)
                table[i] = 1; // short string
            else if (i <= 0xBF)
                table[i] = (byte)(1 + (i - 0xB7)); // long string (2..9)
            else if (i <= 0xF7)
                table[i] = 1; // short list
            else
                table[i] = (byte)(1 + (i - 0xF7)); // long list (2..9)
        }
        return table;
    }

    private static sbyte[] BuildPrefixLenTable()
    {
        sbyte[] table = new sbyte[byte.MaxValue];
        for (int i = 0; i < table.Length; i++)
        {
            if (i < 128)
                table[i] = 0;  // single byte
            else if (i <= 183)
                table[i] = 1;  // short string
            else if (i < 192)
                table[i] = -1; // long string marker
            else if (i <= 247)
                table[i] = 1;  // short list
            else
                table[i] = -2; // long list marker
        }
        return table;
    }

    private static sbyte[] BuildContentLenTable()
    {
        sbyte[] table = new sbyte[byte.MaxValue];
        for (int i = 0; i < table.Length; i++)
        {
            if (i < 128)
                table[i] = 1;                 // single byte
            else if (i <= 183)
                table[i] = (sbyte)(i - 0x80); // short string
            else if (i < 192)
                table[i] = -1;                // long string marker
            else if (i <= 247)
                table[i] = (sbyte)(i - 0xc0); // short list
            else
                table[i] = -2;                // long list marker
        }
        return table;
    }

    /// <summary>
    /// Deserializes a length value from a byte reference using unsafe operations.
    /// This is shared between ValueRlpStream, RlpStream, and ValueDecoderContext.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DeserializeLengthRef(ref byte firstElement, int lengthOfLength)
    {
        int result = firstElement;
        if (result == 0)
        {
            ThrowInvalidData();
        }

        if (lengthOfLength == 1)
        {
            // Already read above
            // result = span[0];
        }
        else if (lengthOfLength == 2)
        {
            if (BitConverter.IsLittleEndian)
            {
                result = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ushort>(ref firstElement));
            }
            else
            {
                result = Unsafe.ReadUnaligned<ushort>(ref firstElement);
            }
        }
        else if (lengthOfLength == 3)
        {
            if (BitConverter.IsLittleEndian)
            {
                result = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref firstElement, 1)))
                    | (result << 16);
            }
            else
            {
                result = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref firstElement, 1))
                    | (result << 16);
            }
        }
        else
        {
            if (BitConverter.IsLittleEndian)
            {
                result = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<int>(ref firstElement));
            }
            else
            {
                result = Unsafe.ReadUnaligned<int>(ref firstElement);
            }
        }

        return result;

        [DoesNotReturn]
        static void ThrowInvalidData()
        {
            throw new RlpException("Length starts with 0");
        }
    }

    [DoesNotReturn, StackTraceHidden]
    public static void ThrowUnexpectedByteValue(int buffer0)
        => throw new RlpException($"Unexpected byte value {buffer0}");

    [DoesNotReturn, StackTraceHidden]
    public static void ThrowInvalidLength(int actualLength, int decodedLength)
        => throw new RlpException($"Invalid actual length {actualLength} decoded {decodedLength}");

    [DoesNotReturn, StackTraceHidden]
    public static void ThrowInvalidLength(int lengthOfLength)
        => throw new RlpException($"Invalid length of length = {lengthOfLength}");

    [DoesNotReturn, StackTraceHidden]
    public static void ThrowUnexpectedPrefix(int prefix)
        => throw new RlpException($"Expected a sequence prefix to be in the range of <192, 255> and got {prefix}");

    [DoesNotReturn, StackTraceHidden]
    public static void ThrowSequenceLengthTooLong()
        => throw new RlpException("Expected length of length less than or equal to 4");

    [DoesNotReturn, StackTraceHidden]
    public static void ThrowUnexpectedLength(int length)
        => throw new RlpException($"Expected length greater than or equal to 56 and was {length}");

    [DoesNotReturn, StackTraceHidden]
    public static uint ThrowNonCanonicalInteger(int position)
        => throw new RlpException($"Non-canonical integer (leading zero bytes) at position {position}");

    [DoesNotReturn, StackTraceHidden]
    public static uint ThrowUnexpectedIntegerLength(int length)
        => throw new RlpException($"Unexpected length of long value: {length}");
}
