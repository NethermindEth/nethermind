// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
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
    /// Parse a UTF-8 span that may be hex ("0x...") or decimal into <typeparamref name="T"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T Parse<T>(ReadOnlySpan<byte> s) where T : struct, INumberBase<T>
    {
        if (s.Length == 0)
        {
            ThrowNullAssignment(typeof(T).Name);
        }

        if (s.SequenceEqual("0x0"u8))
        {
            return T.Zero;
        }

        if (s.StartsWith("0x"u8))
        {
            s = s[2..];
            if (T.TryParse(s, NumberStyles.AllowHexSpecifier, null, out T value))
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

    /// <summary>
    /// Write a numeric value according to the current <see cref="NumberConversion"/> mode.
    /// </summary>
    [SkipLocalsInit]
    internal static void Write<T>(Utf8JsonWriter writer, T value) where T : struct, INumberBase<T>
    {
        switch (ForcedNumberConversion.GetFinalConversion())
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
