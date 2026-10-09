// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Nethermind.Blockchain.Tracing.GethStyle;

internal sealed class GethTraceOptionsConverter : JsonConverter<GethTraceOptions>
{
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> WireFallbacks = [];

    public override GethTraceOptions Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<WireOptions>(ref reader, WireSerializerOptions(options))!;

    public override void Write(Utf8JsonWriter writer, GethTraceOptions value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value as WireOptions ?? new WireOptions(value), WireSerializerOptions(options));

    private static JsonSerializerOptions WireSerializerOptions(JsonSerializerOptions options) =>
        WireFallbacks.GetValue(options, static original => new(original)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        });

    private sealed record WireOptions : GethTraceOptions
    {
        public WireOptions() { }
        public WireOptions(GethTraceOptions options) : base(options) { }

        public new string? Timeout
        {
            get => TimeoutText ?? (base.Timeout is { } duration ? duration.Ticks.ToString(CultureInfo.InvariantCulture) + "00ns" : null);
            init => TimeoutText = value;
        }
    }
}
