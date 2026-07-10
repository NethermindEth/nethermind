// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;

namespace Nethermind.Serialization.Json;

public class AddressAsKeyConverter : JsonConverter<AddressAsKey>
{
    public override AddressAsKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        Address? address = AddressConverter.ReadAddress(ref reader);
        return address is null ? throw new JsonException("Invalid address key") : new AddressAsKey(address);
    }

    public override void Write(Utf8JsonWriter writer, AddressAsKey value, JsonSerializerOptions options) =>
        ByteArrayConverter.Convert(writer, value.Value.Bytes, skipLeadingZeros: false);

    public override AddressAsKey ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(AddressConverter.ReadAddressPropertyName(ref reader));

    public override void WriteAsPropertyName(Utf8JsonWriter writer, AddressAsKey value, JsonSerializerOptions options) =>
        AddressConverter.WriteAddressPropertyName(writer, value.Value);
}
