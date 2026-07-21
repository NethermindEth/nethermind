// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Json;

public class AddressConverter(bool strictHexFormat = false) : JsonConverter<Address>
{
    public AddressConverter() : this(false) { }

    private readonly bool _strictHexFormat = strictHexFormat;

    public override Address? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) => ReadAddress(ref reader, _strictHexFormat);

    internal static Address? ReadAddress(ref Utf8JsonReader reader, bool strictHexFormat = false)
    {
        Span<byte> bytes = stackalloc byte[Address.Size];
        if (ByteArrayConverter.TryConvertToExactLength(ref reader, bytes, strictHexFormat))
        {
            return new Address(bytes);
        }

        byte[]? addressBytes = ByteArrayConverter.ConvertData(ref reader, strictHexFormat);
        return addressBytes is null ? null : new Address(addressBytes);
    }

    public override void Write(
        Utf8JsonWriter writer,
        Address address,
        JsonSerializerOptions options) => ByteArrayConverter.Convert(writer, address.Bytes, skipLeadingZeros: false);

    public override Address ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ReadAddressPropertyName(ref reader, _strictHexFormat);

    [SkipLocalsInit]
    public override void WriteAsPropertyName(Utf8JsonWriter writer,
        Address value,
        JsonSerializerOptions options) => WriteAddressPropertyName(writer, value);

    [SkipLocalsInit]
    internal static Address ReadAddressPropertyName(ref Utf8JsonReader reader, bool strictHexFormat = false)
    {
        Span<byte> bytes = stackalloc byte[Address.Size];
        if (ByteArrayConverter.TryConvertToExactLength(ref reader, bytes, strictHexFormat))
        {
            return new Address(bytes);
        }

        return new Address(ByteArrayConverter.ConvertData(ref reader, strictHexFormat) ?? throw new JsonException("Invalid address property name"));
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

}
