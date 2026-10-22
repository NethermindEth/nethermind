// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Exceptions;
using Nethermind.Int256;

namespace Nethermind.Serialization.Json;

[JsonConverter(typeof(StorageIndexConverter))]
public readonly record struct StorageIndex(UInt256 Value)
{
    public static implicit operator UInt256(StorageIndex index) => index.Value;
}

public sealed class StorageIndexConverter : JsonConverter<StorageIndex>
{
    private const int MaxLength = 2 + 64;

    [SkipLocalsInit]
    public override StorageIndex Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(ReadValue(ref reader));

    [SkipLocalsInit]
    internal static UInt256 ReadValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException();
        }

        int length = reader.HasValueSequence ? (int)reader.ValueSequence.Length : reader.ValueSpan.Length;
        if (length == 0)
        {
            throw new JsonException();
        }

        if (length > MaxLength)
        {
            ThrowInvalidKey("storage key too long (want at most 32 bytes)", ref reader);
        }

        try
        {
            if (reader.HasValueSequence)
            {
                Span<byte> span = stackalloc byte[length];
                reader.ValueSequence.CopyTo(span);
                return Parse(span);
            }

            return Parse(reader.ValueSpan);
        }
        catch (FormatException)
        {
            ThrowInvalidKey("invalid hex in storage key", ref reader);
            return default;
        }
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidKey(string reason, ref Utf8JsonReader reader) =>
        throw new SafePublicMessageFormatException($"{reason}: \"{reader.GetString()}\"", omitArgumentPosition: true);

    private static UInt256 Parse(ReadOnlySpan<byte> hex)
    {
        if (!hex.StartsWith("0x"u8))
        {
            throw new JsonException();
        }

        return UInt256Converter.ReadHex(hex);
    }

    public override void Write(Utf8JsonWriter writer, StorageIndex value, JsonSerializerOptions options) =>
        HexWriter.WriteUInt256HexRawValue(writer, value.Value);
}
