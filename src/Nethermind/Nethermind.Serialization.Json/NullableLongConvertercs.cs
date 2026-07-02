// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Serialization.Json;

public class NullableLongConverter : NullableJsonConverter<long>
{
    public NullableLongConverter() : base(new LongConverter()) { }
    public NullableLongConverter(bool strictQuantity) : base(new LongConverter(strictQuantity)) { }
}

public class NullableRawLongConverter : JsonConverter<long?>
{
    private readonly LongConverter _converter = new();

    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : _converter.Read(ref reader, typeToConvert, options);

    public override void Write(
        Utf8JsonWriter writer,
        long? value,
        JsonSerializerOptions options)
    {
        if (!value.HasValue)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteNumberValue(value.GetValueOrDefault());
        }
    }
}

public class NullableRawULongConverter : JsonConverter<ulong?>
{
    private readonly ULongConverter _converter = new();

    public override ulong? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : _converter.Read(ref reader, typeToConvert, options);

    public override void Write(
        Utf8JsonWriter writer,
        ulong? value,
        JsonSerializerOptions options)
    {
        if (!value.HasValue)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteNumberValue(value.GetValueOrDefault());
        }
    }
}
