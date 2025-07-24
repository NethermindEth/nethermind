// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Json
{
    public class EthereumJsonSerializer : IJsonSerializer
    {
        public const int DefaultMaxDepth = 128;
        private readonly int? _maxDepth;
        private readonly JsonSerializerOptions _jsonOptions;

        public EthereumJsonSerializer(IEnumerable<JsonConverter> converters, int maxDepth = DefaultMaxDepth)
        {
            _maxDepth = maxDepth;
            _jsonOptions = CreateOptions(indented: false, maxDepth: maxDepth, converters: converters);
        }

        public EthereumJsonSerializer(int maxDepth = DefaultMaxDepth)
        {
            _maxDepth = maxDepth;
            _jsonOptions = maxDepth != DefaultMaxDepth ? CreateOptions(indented: false, maxDepth: maxDepth) : JsonOptions;
        }

        public object Deserialize(string json, Type type)
        {
            return JsonSerializer.Deserialize(json, type, _jsonOptions);
        }

        public T Deserialize<T>(Stream stream)
        {
            return JsonSerializer.Deserialize<T>(stream, _jsonOptions);
        }

        public T Deserialize<T>(string json)
        {
            return JsonSerializer.Deserialize<T>(json, _jsonOptions);
        }

        public T Deserialize<T>(ref Utf8JsonReader json)
        {
            return JsonSerializer.Deserialize<T>(ref json, _jsonOptions);
        }

        public string Serialize<T>(T value, bool indented = false)
        {
            return JsonSerializer.Serialize<T>(value, indented ? JsonOptionsIndented : _jsonOptions);
        }

        private static JsonSerializerOptions CreateOptions(bool indented, IEnumerable<JsonConverter> converters = null, int maxDepth = DefaultMaxDepth)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = indented,
                NewLine = "\n",
                IncludeFields = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                MaxDepth = maxDepth,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Converters =
                {
                    new LongConverter(),
                    new UInt256Converter(),
                    new ULongConverter(),
                    new IntConverter(),
                    new ByteArrayConverter(),
                    new ByteReadOnlyMemoryConverter(),
                    new NullableByteReadOnlyMemoryConverter(),
                    new NullableLongConverter(),
                    new NullableULongConverter(),
                    new NullableUInt256Converter(),
                    new NullableIntConverter(),
                    new TxTypeConverter(),
                    new DoubleConverter(),
                    new DoubleArrayConverter(),
                    new BooleanConverter(),
                    new DictionaryAddressKeyConverter(),
                    new MemoryByteConverter(),
                    new BigIntegerConverter(),
                    new NullableBigIntegerConverter(),
                    new JavaScriptObjectConverter(),
                }
            };

            options.Converters.AddRange(_additionalConverters);
            options.Converters.AddRange(converters ?? Array.Empty<JsonConverter>());

            return options;
        }

        private static readonly List<JsonConverter> _additionalConverters = new();
        public static void AddConverter(JsonConverter converter)
        {
            _additionalConverters.Add(converter);

            JsonOptions = CreateOptions(indented: false);
            JsonOptionsIndented = CreateOptions(indented: true);
        }

        public static JsonSerializerOptions JsonOptions { get; private set; } = CreateOptions(indented: false);

        public static JsonSerializerOptions JsonOptionsIndented { get; private set; } = CreateOptions(indented: true);

        private static readonly StreamPipeWriterOptions optionsLeaveOpen = new(pool: MemoryPool<byte>.Shared, minimumBufferSize: 4096, leaveOpen: true);
        private static readonly StreamPipeWriterOptions options = new(pool: MemoryPool<byte>.Shared, minimumBufferSize: 4096, leaveOpen: false);

        private static CountingStreamPipeWriter GetPipeWriter(Stream stream, bool leaveOpen)
        {
            return new CountingStreamPipeWriter(stream, leaveOpen ? optionsLeaveOpen : options);
        }

        public long Serialize<T>(Stream stream, T value, bool indented = false, bool leaveOpen = true)
        {
            var countingWriter = GetPipeWriter(stream, leaveOpen);
            using var writer = new Utf8JsonWriter(countingWriter, CreateWriterOptions(indented));
            JsonSerializer.Serialize(writer, value, indented ? JsonOptionsIndented : _jsonOptions);
            countingWriter.Complete();

            long outputCount = countingWriter.WrittenCount;
            return outputCount;
        }

        private JsonWriterOptions CreateWriterOptions(bool indented)
        {
            JsonWriterOptions writerOptions = new JsonWriterOptions { SkipValidation = true, Indented = indented };
            writerOptions.MaxDepth = _maxDepth ?? writerOptions.MaxDepth;
            return writerOptions;
        }

        public async ValueTask<long> SerializeAsync<T>(Stream stream, T value, CancellationToken cancellationToken, bool indented = false, bool leaveOpen = true)
        {
            var writer = GetPipeWriter(stream, leaveOpen);
            await JsonSerializer.SerializeAsync(writer, value, indented ? JsonOptionsIndented : _jsonOptions, cancellationToken);
            await writer.CompleteAsync();

            long outputCount = writer.WrittenCount;
            return outputCount;
        }

        public Task SerializeAsync<T>(PipeWriter writer, T value, bool indented = false)
            => JsonSerializer.SerializeAsync(writer, value, indented ? JsonOptionsIndented : _jsonOptions);

        public static void SerializeToStream<T>(Stream stream, T value, bool indented = false)
        {
            JsonSerializer.Serialize(stream, value, indented ? JsonOptionsIndented : JsonOptions);
        }
    }

    public static class JsonElementExtensions
    {
        public static bool TryGetSubProperty(this JsonElement element, string innerPath, out JsonElement value)
        {
            ArgumentNullException.ThrowIfNullOrEmpty(innerPath);

            ReadOnlySpan<char> pathSpan = innerPath.AsSpan();
            int lastDot = pathSpan.LastIndexOf('.');
            if (lastDot >= 0)
            {
                JsonElement currentElement = element;
                foreach (Range subPath in pathSpan[..lastDot].Split('.'))
                {
                    if (!currentElement.TryGetProperty(pathSpan[subPath], out currentElement))
                    {
                        value = default;
                        return false;
                    }
                }
                lastDot++;
                return currentElement.TryGetProperty(pathSpan[lastDot..], out value);
            }

            return element.TryGetProperty(pathSpan, out value);
        }
    }
}
