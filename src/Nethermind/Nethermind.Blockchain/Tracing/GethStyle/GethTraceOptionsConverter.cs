// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Blockchain.Tracing.GethStyle;

internal sealed class GethTraceOptionsConverter : JsonConverter<GethTraceOptions>
{
    public override GethTraceOptions Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<WireOptions>(ref reader, options)!;

    public override void Write(Utf8JsonWriter writer, GethTraceOptions value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value as WireOptions ?? new WireOptions(value), options);

    internal sealed record WireOptions : GethTraceOptions
    {
        public WireOptions() { }
        public WireOptions(GethTraceOptions options) : base(options) { }

        [JsonConverter(typeof(RawDurationConverter))]
        public new string? Timeout
        {
            get => TimeoutText ?? (base.Timeout is { } duration ? duration.Ticks.ToString(CultureInfo.InvariantCulture) + "00ns" : null);
            init => TimeoutText = value;
        }
    }

    internal sealed class RawDurationConverter : JsonConverter<string?>
    {
        public override bool HandleNull => true;

        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            CustomTimeDurationConverter.ReadRawString(ref reader);

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }
}
