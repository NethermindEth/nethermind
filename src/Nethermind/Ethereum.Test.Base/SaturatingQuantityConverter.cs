// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Ethereum.Test.Base;

/// <summary>Reads a transaction-template hex quantity, taking one wider than its type as the type's maximum.</summary>
/// <remarks>
/// Static-validity fixtures put a field past its width (a fee of 2^256, a nonce of 2^64) in the template of a
/// transaction they expect to be rejected. The strict converters fail the whole file over that one value, losing
/// every other case in it. Conversion takes such a transaction from its txbytes, where the same field fails to
/// decode, so the template only stands in for it; at the maximum it still describes a transaction that is rejected.
/// Any other value goes to the serializer's own converter, and production keeps rejecting these.
/// </remarks>
public abstract class SaturatingQuantityConverter<T>(int maxHexDigits, T max) : JsonConverter<T> where T : struct
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        IsBeyondWidth(ref reader) ? max : Default(options).Read(ref reader, typeToConvert, options);

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        Default(options).Write(writer, value, options);

    private static JsonConverter<T> Default(JsonSerializerOptions options) => (JsonConverter<T>)options.GetConverter(typeof(T));

    private bool IsBeyondWidth(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String || reader.HasValueSequence)
        {
            return false;
        }

        ReadOnlySpan<byte> value = reader.ValueSpan;
        if (!value.StartsWith("0x"u8))
        {
            return false;
        }

        ReadOnlySpan<byte> digits = value[2..].TrimStart((byte)'0');
        return digits.Length > maxHexDigits && !digits.ContainsAnyExcept("0123456789abcdefABCDEF"u8);
    }
}

public sealed class SaturatingUInt256Converter() : SaturatingQuantityConverter<UInt256>(maxHexDigits: 64, UInt256.MaxValue);

public sealed class SaturatingNullableUInt256Converter() : NullableJsonConverter<UInt256>(new SaturatingUInt256Converter());

public sealed class SaturatingULongConverter() : SaturatingQuantityConverter<ulong>(maxHexDigits: 16, ulong.MaxValue);
