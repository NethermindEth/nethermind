// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Json
{
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public class DoubleConverter : JsonConverter<double>
    {
        public override double Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            return reader.GetDouble();
        }

        [SkipLocalsInit]
        public override void Write(
            Utf8JsonWriter writer,
            double value,
            JsonSerializerOptions options)
        {
            writer.WriteRawValue(value.ToString("0.0#########"), skipInputValidation: true);
        }
    }

    public class DoubleArrayConverter : JsonConverter<double[]>
    {
        public override double[] Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                string s = reader.GetString();
                return JsonSerializer.Deserialize<double[]>(s);
            }

            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException();
            }
            List<double> values = null;
            reader.Read();
            while (reader.TokenType == JsonTokenType.Number)
            {
                values ??= new List<double>();
                values.Add(reader.GetDouble());
            }
            if (reader.TokenType != JsonTokenType.EndArray)
            {
                throw new JsonException();
            }
            reader.Read();
            return values?.ToArray() ?? Array.Empty<double>();
        }

        [SkipLocalsInit]
        public override void Write(
            Utf8JsonWriter writer,
            double[] values,
            JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (double value in values)
            {
                writer.WriteRawValue(value.ToString("0.0#########"), skipInputValidation: true);
            }
            writer.WriteEndArray();
        }
    }
}
