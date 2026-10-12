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
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Trace options must be an object.");

        JsonTypeInfo wireTypeInfo = options.GetTypeInfo(typeof(WireOptions));
        StringComparison nameComparison = options.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
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

        [JsonConverter(typeof(RawDurationConverter))]
        public new string? Timeout
        {
            get => TimeoutText ?? (base.Timeout is { } duration ? CustomTimeDurationConverter.Format(duration) : null);
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
