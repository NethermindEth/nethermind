// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nethermind.Core;

namespace Nethermind.Serialization.Json;

public class AddressConverter : JsonConverter<Address>
{
    public override Address? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        byte[]? bytes = ByteArrayConverter.Convert(ref reader);
        return bytes is null ? null : new Address(bytes);
    }

    public override void Write(
        Utf8JsonWriter writer,
        Address address,
        JsonSerializerOptions options)
    {
        ByteArrayConverter.Convert(writer, address.Bytes, skipLeadingZeros: false);
    }
}
