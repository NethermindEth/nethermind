// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Serialization.Json;

/// <summary>Reads a 64-bit <c>QUANTITY</c> only in its canonical form: a <c>0x</c>-prefixed hex string without
/// leading zeros, <c>0x0</c> for zero (execution-apis common.md).</summary>
/// <remarks>Applies whatever <c>JsonRpc.StrictHexFormat</c> is set to, for a field whose encoding identifies
/// something, as an omission claim's does an inclusion check.</remarks>
public sealed class CanonicalQuantityConverter : JsonConverter<ulong>
{
    private const int MaxDigits = sizeof(ulong) * 2;

    public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        ReadOnlySpan<char> digits = text is not null && text.StartsWith("0x", StringComparison.Ordinal) ? text.AsSpan(2) : default;
        if (digits.IsEmpty || digits.Length > MaxDigits || (digits.Length > 1 && digits[0] == '0')
            || !ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong value))
            throw new JsonException("QUANTITY must be 0x-prefixed hex without leading zeros");
        return value;
    }

    public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options) =>
        writer.WriteStringValue($"0x{value:x}");
}
