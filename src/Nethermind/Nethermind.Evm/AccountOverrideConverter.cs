// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Evm;

/// <summary>
/// Reads an <see cref="AccountOverride"/> and rejects a key that names no field of it, or two keys that name the same
/// field, as the spec requires. Keys match fields case-insensitively.
/// </summary>
public sealed class AccountOverrideConverter : JsonConverter<AccountOverride>
{
    private const int Nonce = 0;
    private const int Balance = 1;
    private const int Code = 2;
    private const int MovePrecompileToAddress = 3;
    private const int State = 4;
    private const int StateDiff = 5;

    private static readonly OverrideFields Fields = OverrideFields.Of(
        nameof(AccountOverride.Nonce),
        nameof(AccountOverride.Balance),
        nameof(AccountOverride.Code),
        nameof(AccountOverride.MovePrecompileToAddress),
        nameof(AccountOverride.State),
        nameof(AccountOverride.StateDiff));

    public override AccountOverride Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected an object for a state override, got {reader.TokenType}.");
        }

        AccountOverride result = new();
        ulong seen = 0;
        Span<ulong> upperCase = stackalloc ulong[Fields.Count];
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            int field = Fields.Match(reader, ref seen, upperCase, "state override");
            reader.Read();
            switch (field)
            {
                case Nonce:
                    result.Nonce = TypeInfoJsonSerializer.Deserialize<ulong?>(ref reader, options);
                    break;
                case Balance:
                    result.Balance = TypeInfoJsonSerializer.Deserialize<UInt256?>(ref reader, options);
                    break;
                case Code:
                    result.Code = TryReadCachedCode(ref reader, out byte[]? cached)
                        ? cached
                        : TypeInfoJsonSerializer.Deserialize<byte[]>(ref reader, options);
                    break;
                case MovePrecompileToAddress:
                    result.MovePrecompileToAddress = TypeInfoJsonSerializer.Deserialize<Address>(ref reader, options);
                    break;
                case State:
                    result.State = TypeInfoJsonSerializer.Deserialize<Dictionary<UInt256, Hash256>>(ref reader, options);
                    break;
                case StateDiff:
                    result.StateDiff = TypeInfoJsonSerializer.Deserialize<Dictionary<UInt256, Hash256>>(ref reader, options);
                    break;
            }
        }

        if (result.State?.ContainsValue(null!) == true || result.StateDiff?.ContainsValue(null!) == true)
            throw new JsonException("Storage override values must not be null.");

        return result;
    }

    /// <summary>Gets the array <see cref="OverrideCodeCache"/> already holds for the code the reader is on.</summary>
    /// <remarks>
    /// A repeated override code then costs no array of its own. Only a contiguous, unescaped hex string of whole bytes
    /// is looked up; any other token, and invalid hex, is left to the byte array converter and its errors.
    /// </remarks>
    private static bool TryReadCachedCode(ref Utf8JsonReader reader, [NotNullWhen(true)] out byte[]? cached)
    {
        cached = null;
        if (reader.TokenType != JsonTokenType.String || reader.HasValueSequence || reader.ValueIsEscaped) return false;

        ReadOnlySpan<byte> hex = reader.ValueSpan;
        if (hex.StartsWith("0x"u8)) hex = hex[2..];
        if (hex.Length == 0 || (hex.Length & 1) != 0 || hex.Length > 2 * OverrideCodeCache.MaxCachedLength) return false;

        byte[] scratch = ArrayPool<byte>.Shared.Rent(hex.Length >> 1);
        Span<byte> code = scratch.AsSpan(0, hex.Length >> 1);
        bool found = HexConverter.TryDecodeFromUtf8(hex, code) && OverrideCodeCache.TryGetCached(code, out cached);
        ArrayPool<byte>.Shared.Return(scratch);
        return found;
    }

    public override void Write(Utf8JsonWriter writer, AccountOverride value, JsonSerializerOptions options)
    {
        bool skipNull = options.DefaultIgnoreCondition == JsonIgnoreCondition.WhenWritingNull;
        writer.WriteStartObject();
        WriteProperty(writer, nameof(AccountOverride.Nonce), value.Nonce, skipNull, options);
        WriteProperty(writer, nameof(AccountOverride.Balance), value.Balance, skipNull, options);
        WriteProperty(writer, nameof(AccountOverride.Code), value.Code, skipNull, options);
        WriteProperty(writer, nameof(AccountOverride.MovePrecompileToAddress), value.MovePrecompileToAddress, skipNull, options);
        WriteProperty(writer, nameof(AccountOverride.State), value.State, skipNull, options);
        WriteProperty(writer, nameof(AccountOverride.StateDiff), value.StateDiff, skipNull, options);
        writer.WriteEndObject();
    }

    private static void WriteProperty<TValue>(Utf8JsonWriter writer, string name, TValue value, bool skipNull, JsonSerializerOptions options)
    {
        if (value is null && skipNull) return;

        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName(name) ?? name);
        TypeInfoJsonSerializer.Serialize(writer, value, options);
    }
}
