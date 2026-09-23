// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Json;

/// <summary>
/// Shared helpers for numeric JSON converters to avoid duplicating hex parsing and
/// <see cref="NumberConversion"/> write logic across int/long/ulong converters.
/// </summary>
public static class NumericConverterHelper
{
    /// <summary>
    /// Parse a UTF-8 span that may be hex ("0x...") or decimal into <typeparamref name="T"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Parse<T>(ReadOnlySpan<byte> s) where T : struct, INumberBase<T>
    {
        if (s.Length == 0)
        {
            ThrowNullAssignment(typeof(T).Name);
        }

        // Fast path for the canonical zero — skips TryParse for the most common JSON-RPC value.
        if (s.SequenceEqual("0x0"u8))
        {
            return T.Zero;
        }

        if (s.StartsWith("0x"u8))
        {
            if (TryParseHex(s[2..], out T value))
            {
                return value;
            }
        }
        else if (T.TryParse(s, NumberStyles.Integer, null, out T value))
        {
            return value;
        }

        ThrowHexConversion(typeof(T).Name);
        return default;
    }

    /// <remarks>
    /// Integers that fit their width decode straight into a big-endian buffer; longer digit strings (leading zeros)
    /// and other number types keep the runtime's parser, and with it its overflow rules.
    /// </remarks>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseHex<T>(ReadOnlySpan<byte> digits, out T value) where T : struct, INumberBase<T>
    {
        // The runtime parser ignores trailing NULs; match it so acceptance does not depend on the digit count.
        if (!digits.IsEmpty && digits[^1] == 0)
        {
            digits = digits.TrimEnd((byte)0);
        }

        bool isPrimitiveInteger = typeof(T) == typeof(long) || typeof(T) == typeof(ulong) || typeof(T) == typeof(int) || typeof(T) == typeof(uint);
        if (isPrimitiveInteger && (uint)(digits.Length - 1) < (uint)(Unsafe.SizeOf<T>() * 2))
        {
            ulong bigEndian = 0;
            Span<byte> buffer = MemoryMarshal.AsBytes(new Span<ulong>(ref bigEndian));
            if (!HexConverter.TryDecodeFromUtf8(digits, buffer[(8 - ((digits.Length + 1) >> 1))..]))
            {
                value = default;
                return false;
            }
            value = T.CreateTruncating(BinaryPrimitives.ReverseEndianness(bigEndian));
            return true;
        }

        return T.TryParse(digits, NumberStyles.AllowHexSpecifier, null, out value);
    }

    /// <summary>
    /// Write a numeric value according to the current <see cref="NumberConversion"/> mode.
    /// </summary>
    [SkipLocalsInit]
    internal static void Write<T>(Utf8JsonWriter writer, T value) where T : struct, INumberBase<T>
    {
        switch (ForcedNumberConversion.Value)
        {
            case NumberConversion.Hex:
                if (value == T.Zero)
                {
                    writer.WriteStringValue("0x0"u8);
                }
                else
                {
                    HexWriter.WriteUlongHexRawValue(writer, ulong.CreateTruncating(value));
                }
                break;
            case NumberConversion.Decimal:
                Span<byte> decBuffer = stackalloc byte[20];
                value.TryFormat(decBuffer, out int decBytesWritten, default, CultureInfo.InvariantCulture);
                writer.WriteStringValue(decBuffer[..decBytesWritten]);
                break;
            case NumberConversion.Raw:
                writer.WriteNumberValue(ulong.CreateTruncating(value));
                break;
            default:
                ThrowNotSupportedConversion();
                break;
        }
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowNullAssignment(string typeName) =>
        throw new JsonException($"null cannot be assigned to {typeName}");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowHexConversion(string typeName) =>
        throw new JsonException($"hex to {typeName}");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowNotSupportedConversion() =>
        throw new NotSupportedException();
}
