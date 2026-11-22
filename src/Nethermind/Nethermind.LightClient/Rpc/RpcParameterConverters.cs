// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.LightClient.Rpc;

internal static class RpcParameterConverters
{
    public sealed class StrictAddressConverter : JsonConverter<Address>
    {
        private static readonly AddressConverter Converter = new(strictHexFormat: true);
        public override Address? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => ReadParameter(ref reader, type, options, Converter);
        public override void Write(Utf8JsonWriter writer, Address value, JsonSerializerOptions options) => Converter.Write(writer, value, options);
    }

    public sealed class StrictHashConverter : JsonConverter<Hash256>
    {
        private static readonly Hash256Converter Converter = new(strictHexFormat: true);
        public override Hash256? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => ReadParameter(ref reader, type, options, Converter);
        public override void Write(Utf8JsonWriter writer, Hash256 value, JsonSerializerOptions options) => Converter.Write(writer, value, options);
    }

    public sealed class StrictQuantityConverter : JsonConverter<UInt256>
    {
        private static readonly UInt256Converter Converter = new(strictQuantity: true);
        public override UInt256 Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String || (reader.ValueIsEscaped || reader.HasValueSequence
                    ? !reader.GetString()!.StartsWith("0x", StringComparison.Ordinal)
                    : !reader.ValueSpan.StartsWith("0x"u8))) throw new JsonException("Expected a hexadecimal quantity.");
            return ReadParameter(ref reader, type, options, Converter);
        }
        public override void Write(Utf8JsonWriter writer, UInt256 value, JsonSerializerOptions options) => Converter.Write(writer, value, options);
    }

    public sealed class StrictDataConverter : JsonConverter<byte[]>
    {
        public override byte[]? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (!reader.ValueIsEscaped) return ByteArrayConverter.ConvertData(ref reader, strictHexFormat: true);
            Utf8JsonReader unescaped = Unescape(ref reader);
            return ByteArrayConverter.ConvertData(ref unescaped, strictHexFormat: true);
        }
        public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options) => ByteArrayConverter.Convert(writer, value, skipLeadingZeros: false);
    }

    public sealed class StorageIndexParameterConverter : JsonConverter<StorageIndex>
    {
        private static readonly StorageIndexConverter Converter = new();
        public override StorageIndex Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => ReadParameter(ref reader, type, options, Converter);
        public override void Write(Utf8JsonWriter writer, StorageIndex value, JsonSerializerOptions options) => Converter.Write(writer, value, options);
    }

    public sealed class StorageKeysConverter : JsonConverter<StorageIndex[]>
    {
        private static readonly JsonSerializerOptions Options = new() { Converters = { new StorageIndexParameterConverter() } };
        public override StorageIndex[]? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => JsonSerializer.Deserialize<StorageIndex[]>(ref reader, Options);
        public override void Write(Utf8JsonWriter writer, StorageIndex[] value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value, Options);
    }

    private static T? ReadParameter<T>(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options, JsonConverter<T> converter)
    {
        if (!reader.ValueIsEscaped) return converter.Read(ref reader, type, options);
        Utf8JsonReader unescaped = Unescape(ref reader);
        return converter.Read(ref unescaped, type, options);
    }

    private static Utf8JsonReader Unescape(ref Utf8JsonReader reader)
    {
        Utf8JsonReader unescaped = new(JsonSerializer.SerializeToUtf8Bytes(reader.GetString()));
        unescaped.Read();
        return unescaped;
    }
}
