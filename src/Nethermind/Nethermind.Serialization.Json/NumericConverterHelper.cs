// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Text;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Nethermind.Serialization.Json;

/// <summary>
/// Shared helpers for numeric JSON converters to avoid duplicating hex parsing and
/// <see cref="NumberConversion"/> write logic across int/long/ulong converters.
/// </summary>
internal static class NumericConverterHelper
{
    /// <summary>
    /// Parse a UTF-8 span that may be hex ("0x...") or decimal into an <see cref="int"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ParseInt(ReadOnlySpan<byte> s)
    {
        if (s.Length == 0)
        {
            ThrowNullAssignment("int");
        }

        if (s.SequenceEqual("0x0"u8))
        {
            return 0;
        }

        int value;
        if (s.StartsWith("0x"u8))
        {
            s = s[2..];
            if (Utf8Parser.TryParse(s, out value, out _, 'x'))
            {
                return value;
            }
        }
        else if (Utf8Parser.TryParse(s, out value, out _))
        {
            return value;
        }

        ThrowHexConversion("int");
        return default;
    }

    /// <summary>
    /// Parse a UTF-8 span that may be hex ("0x...") or decimal into a <see cref="long"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long ParseLong(ReadOnlySpan<byte> s)
    {
        if (s.Length == 0)
        {
            ThrowNullAssignment("long");
        }

        if (s.SequenceEqual("0x0"u8))
        {
            return 0L;
        }

        long value;
        if (s.StartsWith("0x"u8))
        {
            s = s[2..];
            if (Utf8Parser.TryParse(s, out value, out _, 'x'))
            {
                return value;
            }
        }
        else if (Utf8Parser.TryParse(s, out value, out _))
        {
            return value;
        }

        ThrowHexConversion("long");
        return default;
    }

    /// <summary>
    /// Parse a UTF-8 span that may be hex ("0x...") or decimal into a <see cref="ulong"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong ParseULong(ReadOnlySpan<byte> s)
    {
        if (s.Length == 0)
        {
            ThrowNullAssignment("ulong");
        }

        if (s.SequenceEqual("0x0"u8))
        {
            return 0uL;
        }

        ulong value;
        if (s.StartsWith("0x"u8))
        {
            s = s[2..];
            if (Utf8Parser.TryParse(s, out value, out _, 'x'))
            {
                return value;
            }
        }
        else if (Utf8Parser.TryParse(s, out value, out _))
        {
            return value;
        }

        ThrowHexConversion("ulong");
        return default;
    }

    /// <summary>
    /// Write a <see cref="long"/> value according to the current <see cref="NumberConversion"/> mode.
    /// </summary>
    [SkipLocalsInit]
    internal static void WriteLong(Utf8JsonWriter writer, long value)
    {
        switch (ForcedNumberConversion.GetFinalConversion())
        {
            case NumberConversion.Hex:
                if (value == 0)
                {
                    writer.WriteStringValue("0x0"u8);
                }
                else
                {
                    HexWriter.WriteUlongHexRawValue(writer, (ulong)value);
                }
                break;
            case NumberConversion.Decimal:
                writer.WriteStringValue(value == 0 ? "0" : value.ToString(CultureInfo.InvariantCulture));
                break;
            case NumberConversion.Raw:
                writer.WriteNumberValue(value);
                break;
            default:
                ThrowNotSupportedConversion();
                break;
        }
    }

    /// <summary>
    /// Write a <see cref="ulong"/> value according to the current <see cref="NumberConversion"/> mode.
    /// </summary>
    [SkipLocalsInit]
    internal static void WriteULong(Utf8JsonWriter writer, ulong value)
    {
        switch (ForcedNumberConversion.GetFinalConversion())
        {
            case NumberConversion.Hex:
                if (value == 0)
                {
                    writer.WriteStringValue("0x0"u8);
                }
                else
                {
                    HexWriter.WriteUlongHexRawValue(writer, value);
                }
                break;
            case NumberConversion.Decimal:
                writer.WriteStringValue(value == 0 ? "0" : value.ToString(CultureInfo.InvariantCulture));
                break;
            case NumberConversion.Raw:
                writer.WriteNumberValue(value);
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
