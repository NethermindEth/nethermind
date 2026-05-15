// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Json;

public class AddressConverter : JsonConverter<Address>
{
    public override Address? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) => ReadAddress(ref reader);

    internal static Address? ReadAddress(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[Address.Size];
        if (TryReadAddressBytes(ref reader, bytes))
        {
            return new Address(bytes);
        }

        byte[]? addressBytes = ByteArrayConverter.Convert(ref reader);
        return addressBytes is null ? null : new Address(addressBytes);
    }

    public override void Write(
        Utf8JsonWriter writer,
        Address address,
        JsonSerializerOptions options) => ByteArrayConverter.Convert(writer, address.Bytes, skipLeadingZeros: false);

    public override Address ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ReadAddressPropertyName(ref reader);

    [SkipLocalsInit]
    public override void WriteAsPropertyName(Utf8JsonWriter writer,
        Address value,
        JsonSerializerOptions options) => WriteAddressPropertyName(writer, value);

    [SkipLocalsInit]
    internal static Address ReadAddressPropertyName(ref Utf8JsonReader reader)
    {
        Span<byte> bytes = stackalloc byte[Address.Size];
        if (TryReadAddressBytes(ref reader, bytes))
        {
            return new Address(bytes);
        }

        return new Address(ByteArrayConverter.Convert(ref reader) ?? throw new JsonException("Invalid address property name"));
    }

    [SkipLocalsInit]
    internal static void WriteAddressPropertyName(Utf8JsonWriter writer, Address value)
    {
        Span<byte> addressBytes = stackalloc byte[Address.Size * 2 + 2];
        addressBytes[0] = (byte)'0';
        addressBytes[1] = (byte)'x';
        Span<byte> hex = addressBytes[2..];
        value.Bytes.OutputBytesToByteHex(hex, false);
        writer.WritePropertyName(addressBytes);
    }

    private static bool TryReadAddressBytes(ref Utf8JsonReader reader, scoped Span<byte> bytes)
    {
        if (reader.HasValueSequence)
        {
            return false;
        }

        ReadOnlySpan<byte> hex = reader.ValueSpan;
        if (hex.Length >= 2 && hex[0] == (byte)'0' && hex[1] == (byte)'x')
        {
            hex = hex[2..];
        }

        if (hex.Length != Address.Size * 2)
        {
            return false;
        }

        Bytes.FromUtf8HexString(hex, bytes);
        return true;
    }
}
