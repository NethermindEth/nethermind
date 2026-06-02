// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Json
{
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
                if (s is null) ThrowExpectedArrayString();
                return JsonSerializer.Deserialize<double[]>(s)
                    ?? throw new JsonException($"Could not deserialize double array from string: {s}");
            }

            if (reader.TokenType != JsonTokenType.StartArray)
                ThrowExpectedStartArray();

            using ArrayPoolListRef<double> values = new(16);
            while (reader.Read() && reader.TokenType == JsonTokenType.Number)
            {
                values.Add(reader.GetDouble());
            }
            if (reader.TokenType != JsonTokenType.EndArray)
                ThrowExpectedEndArray();

            if (values.Count == 0) return [];

            double[] result = new double[values.Count];
            values.CopyTo(result, 0);
            return result;
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
                if (double.IsNaN(value) || double.IsInfinity(value))
                    ThrowNotFiniteJsonException(value);
                writer.WriteRawValue(value.ToString("R", CultureInfo.InvariantCulture), skipInputValidation: true);
            }
            writer.WriteEndArray();
        }

        [DoesNotReturn]
        private static void ThrowExpectedArrayString() =>
            throw new JsonException("Expected a JSON array string, got null.");

        [DoesNotReturn]
        private static void ThrowExpectedStartArray() =>
            throw new JsonException("Expected start of JSON array.");

        [DoesNotReturn]
        private static void ThrowExpectedEndArray() =>
            throw new JsonException("Expected end of JSON array.");

        [DoesNotReturn]
        private static void ThrowNotFiniteJsonException(double value) =>
            throw new JsonException($"The value '{value}' is not a valid JSON number.");
    }
}
