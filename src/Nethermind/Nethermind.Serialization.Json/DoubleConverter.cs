// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Serialization.Json
{
    public class DoubleConverter : JsonConverter<double>
    {
        public override double Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => reader.GetDouble();

        [SkipLocalsInit]
        public override void Write(
            Utf8JsonWriter writer,
            double value,
            JsonSerializerOptions options)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                ThrowNotFiniteJsonException(value);
            writer.WriteRawValue(value.ToString("R", CultureInfo.InvariantCulture), skipInputValidation: true);
        }

        [DoesNotReturn]
        private static void ThrowNotFiniteJsonException(double value) =>
            throw new JsonException($"The value '{value}' is not a valid JSON number.");
    }
}
