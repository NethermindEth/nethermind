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

        // Nullable shadows treat a JSON null as not set, as Go's encoding/json does for non-pointer scalars.
        public new bool? DisableStorage { get => base.DisableStorage; init { if (value is { } v) base.DisableStorage = v; } }

        public new bool? DisableStack { get => base.DisableStack; init { if (value is { } v) base.DisableStack = v; } }

        public new bool? EnableReturnData { get => base.EnableReturnData; init { if (value is { } v) base.EnableReturnData = v; } }

#pragma warning disable CS0618
        public new bool? DisableMemory { get => base.DisableMemory; set { if (value is { } v) base.DisableMemory = v; } }
#pragma warning restore CS0618

        public new bool? EnableMemory { get => base.EnableMemory; set { if (value is { } v) base.EnableMemory = v; } }

        [JsonConverter(typeof(LimitConverter))]
        public new long? Limit { get => base.Limit; init { if (value is { } v) base.Limit = v; } }
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
