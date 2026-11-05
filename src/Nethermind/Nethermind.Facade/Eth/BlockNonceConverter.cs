// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Facade.Eth;

/// <summary>
/// Serializes the 64-bit PoW block nonce as the fixed 8-byte hex DATA string the eth JSON-RPC uses
/// (e.g. <c>0x0000000000000000</c>) rather than the minimal QUANTITY form. A null nonce (AuRa headers,
/// pending blocks) stays <c>null</c>.
/// </summary>
public sealed class BlockNonceConverter : JsonConverter<ulong?>
{
    public override ulong? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        ReadOnlySpan<char> hex = reader.GetString();
        if (hex.IsEmpty) return null;
        if (hex.StartsWith("0x")) hex = hex[2..];
        return ulong.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    public override void Write(Utf8JsonWriter writer, ulong? value, JsonSerializerOptions options)
    {
        if (!value.HasValue)
        {
            writer.WriteNullValue();
            return;
        }

        Span<char> chars = stackalloc char[18];
        chars[0] = '0';
        chars[1] = 'x';
        value.GetValueOrDefault().TryFormat(chars[2..], out _, "x16");
        writer.WriteStringValue(chars);
    }
}
