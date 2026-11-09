// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.DebugModule;

internal sealed class TraceCallOptionsConverter : JsonConverter<GethTraceOptions>
{
    private readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> _options = new();

    public override GethTraceOptions? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<GethTraceOptions>(ref reader, GetOptions(options));

    public override void Write(Utf8JsonWriter writer, GethTraceOptions value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, GetOptions(options));

    private JsonSerializerOptions GetOptions(JsonSerializerOptions options) => _options.GetValue(options, source =>
    {
        JsonSerializerOptions scoped = new(source);
        for (int i = scoped.Converters.Count - 1; i >= 0; i--)
            if (scoped.Converters[i] is TraceCallOptionsConverter) scoped.Converters.RemoveAt(i);
        scoped.Converters.Insert(0, new StorageKeyConverter((JsonConverter<UInt256>)source.GetConverter(typeof(UInt256))));
        return scoped;
    });

    private sealed class StorageKeyConverter(JsonConverter<UInt256> inner) : JsonConverter<UInt256>
    {
        public override UInt256 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            inner.Read(ref reader, typeToConvert, options);

        public override void Write(Utf8JsonWriter writer, UInt256 value, JsonSerializerOptions options) =>
            inner.Write(writer, value, options);

        public override UInt256 ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string key = reader.GetString()!;
            if (key.Length != 66 || key[0] != '0' || key[1] is not ('x' or 'X'))
                throw new JsonException("Storage override keys must be 32-byte hex hashes.");

            try
            {
                return new UInt256(Convert.FromHexString(key.AsSpan(2)), isBigEndian: true);
            }
            catch (FormatException exception)
            {
                throw new JsonException("Storage override keys must be 32-byte hex hashes.", exception);
            }
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, UInt256 value, JsonSerializerOptions options) =>
            inner.WriteAsPropertyName(writer, value, options);
    }
}
