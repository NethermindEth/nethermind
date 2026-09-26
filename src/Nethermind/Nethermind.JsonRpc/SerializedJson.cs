// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc;

/// <summary>A payload serialized once with the options responses are written with, then written as is.</summary>
[JsonConverter(typeof(SerializedJsonConverter))]
internal readonly record struct SerializedJson(byte[] Utf8Json);

internal sealed class SerializedJsonConverter : JsonConverter<SerializedJson>
{
    public override SerializedJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, SerializedJson value, JsonSerializerOptions options) =>
        writer.WriteRawValue(value.Utf8Json, skipInputValidation: true);
}
