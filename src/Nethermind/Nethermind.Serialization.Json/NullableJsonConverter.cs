// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Serialization.Json;

/// <summary>
/// Generic nullable wrapper for any <see cref="JsonConverter{T}"/> where T is a value type.
/// Eliminates duplicated null-handling boilerplate across per-type nullable converters.
/// </summary>
public class NullableJsonConverter<T>(JsonConverter<T> innerConverter) : JsonConverter<T?> where T : struct
{
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null
            ? null
            : innerConverter.Read(ref reader, typeof(T), options);

    public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
    {
        if (!value.HasValue)
        {
            writer.WriteNullValue();
            return;
        }

        innerConverter.Write(writer, value.GetValueOrDefault(), options);
    }
}
