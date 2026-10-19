// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

        [JsonConverter(typeof(ParsedDurationConverter))]
        public new ParsedTraceDuration Timeout
        {
            get => ParsedTimeout;
            init => ParsedTimeout = value;
        }
    }

    internal sealed class ParsedDurationConverter : JsonConverter<ParsedTraceDuration>
    {
        public override ParsedTraceDuration Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            CustomTimeDurationConverter.ReadDuration(ref reader);

        public override void Write(Utf8JsonWriter writer, ParsedTraceDuration value, JsonSerializerOptions options)
        {
            if (value.RawText is { } rawText) writer.WriteStringValue(rawText);
            else if (value.Value is { } duration) CustomTimeDurationConverter.WriteFormatted(writer, duration);
            else writer.WriteNullValue();
        }
    }
}
