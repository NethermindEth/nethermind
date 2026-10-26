// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Nethermind.Blockchain.Tracing.GethStyle;

internal sealed class GethTraceOptionsConverter : JsonConverter<GethTraceOptions>
{
    public override GethTraceOptions Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        JsonReaderOptions readerOptions = reader.CurrentState.Options;
        StringComparison nameComparison = options.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (reader.TokenType == JsonTokenType.StartObject && !RequiresNullFiltering(reader, options.GetTypeInfo(typeof(WireOptions)), nameComparison))
            return JsonSerializer.Deserialize<WireOptions>(ref reader, options)!;

        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Trace options must be an object.");
        JsonTypeInfo wireTypeInfo = options.GetTypeInfo(typeof(WireOptions));

        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Null && IsNonNullableOption(property.Name, wireTypeInfo, nameComparison)) continue;
                writer.WritePropertyName(property.Name);
                writer.WriteRawValue(property.Value.GetRawText(), skipInputValidation: true);
            }
            writer.WriteEndObject();
        }

        Utf8JsonReader normalizedReader = new(buffer.WrittenSpan, readerOptions);
        return JsonSerializer.Deserialize<WireOptions>(ref normalizedReader, options)!;
    }

    private static bool RequiresNullFiltering(Utf8JsonReader reader, JsonTypeInfo typeInfo, StringComparison comparison)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            Utf8JsonReader propertyReader = reader;
            if (!reader.Read()) return false;
            if (reader.TokenType == JsonTokenType.Null && IsNonNullableOption(propertyReader.GetString()!, typeInfo, comparison)) return true;
            if (!reader.TrySkip()) return false;
        }

        return false;
    }

    private static bool IsNonNullableOption(string name, JsonTypeInfo typeInfo, StringComparison comparison)
    {
        foreach (JsonPropertyInfo property in typeInfo.Properties)
        {
            if ((property.PropertyType == typeof(bool) || property.PropertyType == typeof(long)) && name.Equals(property.Name, comparison))
                return true;
        }

        return false;
    }

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
