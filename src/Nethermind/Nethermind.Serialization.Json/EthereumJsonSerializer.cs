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
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Json
{
    public sealed class EthereumJsonSerializer : IJsonSerializer
    {
        public const int DefaultMaxDepth = 128;
        private static readonly object _globalOptionsLock = new();

        private static readonly List<JsonConverter> _additionalConverters = new();
        private static readonly List<IJsonTypeInfoResolver> _additionalResolvers = new();
        private static bool _strictHexFormat;
        private static int _optionsVersion;

        private readonly int? _maxDepth;
        private readonly JsonConverter[] _instanceConverters;
        private readonly object _instanceOptionsLock = new();

        private JsonSerializerOptions _jsonOptions = null!;
        private JsonSerializerOptions _jsonOptionsIndented = null!;
        private int _instanceOptionsVersion;

        public EthereumJsonSerializer(IEnumerable<JsonConverter> converters, int maxDepth = DefaultMaxDepth)
        {
            _maxDepth = maxDepth;
            _instanceConverters = CopyConverters(converters);
            RefreshInstanceOptions();
        }

        public EthereumJsonSerializer(int maxDepth = DefaultMaxDepth)
        {
            _maxDepth = maxDepth;
            _instanceConverters = [];
            RefreshInstanceOptions();
        }

        public object Deserialize(string json, Type type)
        {
            return JsonSerializer.Deserialize(json, type, GetSerializerOptions(indented: false));
        }

        public T Deserialize<T>(Stream stream)
        {
            return JsonSerializer.Deserialize<T>(stream, GetSerializerOptions(indented: false));
        }

        public T Deserialize<T>(string json)
        {
            return JsonSerializer.Deserialize<T>(json, GetSerializerOptions(indented: false));
        }

        public T Deserialize<T>(ref Utf8JsonReader json)
        {
            return JsonSerializer.Deserialize<T>(ref json, GetSerializerOptions(indented: false));
        }

        public string Serialize<T>(T value, bool indented = false)
        {
            return JsonSerializer.Serialize<T>(value, GetSerializerOptions(indented));
        }

        private static JsonSerializerOptions CreateOptions(bool indented, IEnumerable<JsonConverter> instanceConverters = null, int maxDepth = DefaultMaxDepth)
        {
            SnapshotGlobalOptions(out bool strictHexFormat, out JsonConverter[] additionalConverters, out IJsonTypeInfoResolver[] additionalResolvers);

            var result = new JsonSerializerOptions
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
                TypeInfoResolver = BuildTypeInfoResolver(additionalResolvers),
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
                    new PublicKeyHashedConverter(),
                    new PublicKeyConverter(),
                    new ValueHash256Converter(strictHexFormat),
                    new Hash256Converter(strictHexFormat),
                }
            };

            result.Converters.AddRange(additionalConverters);
            result.Converters.AddRange(instanceConverters ?? Array.Empty<JsonConverter>());
            return result;
        }

        public static void AddConverter(JsonConverter converter)
        {
            ArgumentNullException.ThrowIfNull(converter);
            lock (_globalOptionsLock)
            {
                _additionalConverters.Add(converter);
                RefreshGlobalOptionsNoLock();
            }
        }

        public static void AddTypeInfoResolver(IJsonTypeInfoResolver resolver)
        {
            ArgumentNullException.ThrowIfNull(resolver);
            lock (_globalOptionsLock)
            {
                for (int i = 0; i < _additionalResolvers.Count; i++)
                {
                    if (ReferenceEquals(_additionalResolvers[i], resolver))
                    {
                        return;
                    }
                }

                _additionalResolvers.Add(resolver);
                RefreshGlobalOptionsNoLock();
            }
        }

        public static bool StrictHexFormat
        {
            get => _strictHexFormat;
            set
            {
                lock (_globalOptionsLock)
                {
                    if (_strictHexFormat == value)
                        return;

                    _strictHexFormat = value;
                    RefreshGlobalOptionsNoLock();
                }
            }
        }

        public static JsonSerializerOptions JsonOptions { get; private set; } = CreateOptions(indented: false);

        public static JsonSerializerOptions JsonOptionsIndented { get; private set; } = CreateOptions(indented: true);

        private static readonly StreamPipeWriterOptions optionsLeaveOpen = new(pool: MemoryPool<byte>.Shared, minimumBufferSize: 16384, leaveOpen: true);
        private static readonly StreamPipeWriterOptions options = new(pool: MemoryPool<byte>.Shared, minimumBufferSize: 16384, leaveOpen: false);

        private static CountingStreamPipeWriter GetPipeWriter(Stream stream, bool leaveOpen)
        {
            return new CountingStreamPipeWriter(stream, leaveOpen ? optionsLeaveOpen : options);
        }

        public long Serialize<T>(Stream stream, T value, bool indented = false, bool leaveOpen = true)
        {
            var countingWriter = GetPipeWriter(stream, leaveOpen);
            using var writer = new Utf8JsonWriter(countingWriter, CreateWriterOptions(indented));
            JsonSerializer.Serialize(writer, value, GetSerializerOptions(indented));
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
            await JsonSerializer.SerializeAsync(writer, value, GetSerializerOptions(indented), cancellationToken);
            await writer.CompleteAsync();

            long outputCount = writer.WrittenCount;
            return outputCount;
        }

        public Task SerializeAsync<T>(PipeWriter writer, T value, bool indented = false)
        {
            using var jsonWriter = new Utf8JsonWriter((IBufferWriter<byte>)writer, CreateWriterOptions(indented));
            JsonSerializer.Serialize(jsonWriter, value, GetSerializerOptions(indented));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Pre-serializes instances to warm System.Text.Json metadata caches at startup.
        /// </summary>
        public static void WarmupSerializer(params object[] instances)
        {
            foreach (object instance in instances)
            {
                _ = JsonSerializer.SerializeToUtf8Bytes(instance, instance.GetType(), JsonOptions);
            }
        }

        public static void SerializeToStream<T>(Stream stream, T value, bool indented = false)
        {
            JsonSerializer.Serialize(stream, value, indented ? JsonOptionsIndented : JsonOptions);
        }

        private JsonSerializerOptions GetSerializerOptions(bool indented)
        {
            EnsureInstanceOptionsCurrent();
            return indented ? _jsonOptionsIndented : _jsonOptions;
        }

        private void EnsureInstanceOptionsCurrent()
        {
            int currentVersion = Volatile.Read(ref _optionsVersion);
            if (_instanceOptionsVersion == currentVersion)
            {
                return;
            }

            lock (_instanceOptionsLock)
            {
                if (_instanceOptionsVersion != currentVersion)
                {
                    RefreshInstanceOptions();
                }
            }
        }

        private void RefreshInstanceOptions()
        {
            _jsonOptions = CreateOptions(indented: false, instanceConverters: _instanceConverters, maxDepth: _maxDepth ?? DefaultMaxDepth);
            _jsonOptionsIndented = CreateOptions(indented: true, instanceConverters: _instanceConverters, maxDepth: _maxDepth ?? DefaultMaxDepth);
            _instanceOptionsVersion = Volatile.Read(ref _optionsVersion);
        }

        private static void RefreshGlobalOptionsNoLock()
        {
            JsonOptions = CreateOptions(indented: false);
            JsonOptionsIndented = CreateOptions(indented: true);
            Interlocked.Increment(ref _optionsVersion);
        }

        private static void SnapshotGlobalOptions(out bool strictHexFormat, out JsonConverter[] additionalConverters, out IJsonTypeInfoResolver[] additionalResolvers)
        {
            lock (_globalOptionsLock)
            {
                strictHexFormat = _strictHexFormat;
                additionalConverters = new JsonConverter[_additionalConverters.Count];
                for (int i = 0; i < _additionalConverters.Count; i++)
                {
                    additionalConverters[i] = _additionalConverters[i];
                }

                additionalResolvers = new IJsonTypeInfoResolver[_additionalResolvers.Count];
                for (int i = 0; i < _additionalResolvers.Count; i++)
                {
                    additionalResolvers[i] = _additionalResolvers[i];
                }
            }
        }

        private static IJsonTypeInfoResolver BuildTypeInfoResolver(IReadOnlyList<IJsonTypeInfoResolver> additionalResolvers)
        {
            int additionalResolversCount = additionalResolvers.Count;
            if (additionalResolversCount == 0)
            {
                return new DefaultJsonTypeInfoResolver();
            }

            IJsonTypeInfoResolver[] resolverChain = new IJsonTypeInfoResolver[additionalResolversCount + 1];
            for (int i = 0; i < additionalResolversCount; i++)
            {
                resolverChain[i] = additionalResolvers[i];
            }

            resolverChain[additionalResolversCount] = new DefaultJsonTypeInfoResolver();
            return JsonTypeInfoResolver.Combine(resolverChain);
        }

        private static JsonConverter[] CopyConverters(IEnumerable<JsonConverter> converters)
        {
            ArgumentNullException.ThrowIfNull(converters);

            if (converters is JsonConverter[] convertersArray)
            {
                JsonConverter[] clone = new JsonConverter[convertersArray.Length];
                Array.Copy(convertersArray, clone, convertersArray.Length);
                return clone;
            }

            List<JsonConverter> list = new();
            foreach (JsonConverter converter in converters)
            {
                list.Add(converter);
            }

            return [.. list];
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
