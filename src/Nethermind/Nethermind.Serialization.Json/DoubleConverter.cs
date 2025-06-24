// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Json
{
    using Nethermind.Core.Collections;
    using System.Globalization;
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
            // For eth_feeHistory compatibility: serialize 0.0 as 0 (integer) to match geth behavior
            string formatted = value == 0.0 ? "0" : value.ToString("0.0#########", CultureInfo.InvariantCulture);
            writer.WriteRawValue(formatted, skipInputValidation: true);
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
            using ArrayPoolList<double> values = new ArrayPoolList<double>(16);
            while (reader.Read() && reader.TokenType == JsonTokenType.Number)
            {
                values.Add(reader.GetDouble());
            }
            if (reader.TokenType != JsonTokenType.EndArray)
            {
                throw new JsonException();
            }

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
                // For eth_feeHistory compatibility: serialize 0.0 as 0 (integer) to match geth behavior
                string formatted = value == 0.0 ? "0" : value.ToString("0.0#########", CultureInfo.InvariantCulture);
                writer.WriteRawValue(formatted, skipInputValidation: true);
            }
            writer.WriteEndArray();
        }
    }
}
