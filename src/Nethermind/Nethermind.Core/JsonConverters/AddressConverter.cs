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
        JsonSerializerOptions options)
    {
        var bytes = ByteArrayConverter.Convert(ref reader);
        return bytes is null ? null : new Address(bytes);
    }

    public override void Write(
        Utf8JsonWriter writer,
        Address address,
        JsonSerializerOptions options)
    {
        ByteArrayConverter.Convert(writer, address.Bytes, skipLeadingZeros: false);
    }

    [SkipLocalsInit]
    public override void WriteAsPropertyName(Utf8JsonWriter writer,
        Address value,
        JsonSerializerOptions options)
    {
        Span<byte> addressBytes = stackalloc byte[Address.Size * 2 + 2];
        addressBytes[0] = (byte)'0';
        addressBytes[1] = (byte)'x';
        Span<byte> hex = addressBytes[2..];
        value.Bytes.AsSpan().OutputBytesToByteHex(hex, false);
        writer.WritePropertyName(addressBytes);
    }
}
