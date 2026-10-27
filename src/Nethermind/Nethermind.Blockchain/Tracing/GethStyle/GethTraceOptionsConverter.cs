// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Blockchain.Tracing.GethStyle;

internal sealed class GethTraceOptionsConverter : JsonConverter<GethTraceOptions>
{
    public override GethTraceOptions Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Trace options must be an object.");
        return JsonSerializer.Deserialize<WireOptions>(ref reader, options)!.Materialize();
    }

    public override void Write(Utf8JsonWriter writer, GethTraceOptions value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value as WireOptions ?? new WireOptions(value), options);

    internal sealed record WireOptions : GethTraceOptions
    {
        public WireOptions() { }
        public WireOptions(GethTraceOptions options) : base(options) { }

        private bool? _disableStorage;
        private bool? _enableReturnData;
        private bool? _disableStack;
        private long? _limit;

        // Setters see every duplicate; source generation buffers init-only values until construction.
        public new bool? DisableStorage { get => base.DisableStorage; set => _disableStorage = value ?? _disableStorage; }
        public new bool? EnableReturnData { get => base.EnableReturnData; set => _enableReturnData = value ?? _enableReturnData; }
        public new bool? DisableStack { get => base.DisableStack; set => _disableStack = value ?? _disableStack; }

        [JsonConverter(typeof(LimitConverter))]
        public new long? Limit { get => base.Limit; set => _limit = value ?? _limit; }

        // Keep the public init-only contract; getters above must read the base so later record copies serialize their overrides.
        internal GethTraceOptions Materialize() => ((GethTraceOptions)this) with
        {
            DisableStorage = _disableStorage ?? base.DisableStorage,
            EnableReturnData = _enableReturnData ?? base.EnableReturnData,
            DisableStack = _disableStack ?? base.DisableStack,
            Limit = _limit ?? base.Limit
        };

        [JsonConverter(typeof(ParsedDurationConverter))]
        public new ParsedTraceDuration Timeout
        {
            get => ParsedTimeout;
            init => ParsedTimeout = value;
        }

#pragma warning disable CS0618
        public new bool? DisableMemory { get => base.DisableMemory; set { if (value is { } v) base.DisableMemory = v; } }
#pragma warning restore CS0618

        public new bool? EnableMemory { get => base.EnableMemory; set { if (value is { } v) base.EnableMemory = v; } }
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
