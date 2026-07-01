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
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Json
{
    /// <summary>
    /// Controls source-generated JSON metadata resolver order.
    /// </summary>
    public enum JsonTypeInfoResolverPriority
    {
        /// <summary>Engine API payload metadata. This is the most latency-sensitive RPC path.</summary>
        EngineApi = 0,
        /// <summary>Broad first-party RPC metadata generated at build time.</summary>
        GeneratedRpc = 10,
        /// <summary>Common facade RPC metadata such as block, transaction, and log DTOs.</summary>
        Facade = 20,
        /// <summary>Eth/debug/trace metadata for proofs, traces, and related payloads.</summary>
        EthRpc = 30,
        /// <summary>JSON-RPC response envelope metadata kept for legacy and fallback paths.</summary>
        JsonRpcResponse = 40,
        /// <summary>External or unclassified resolvers registered by plugins.</summary>
        External = 100,
    }

    public sealed class EthereumJsonSerializer : IJsonSerializer
    {
        // Must accommodate the deepest possible callTracer output: each NativeCallTracerCallFrame
        // contributes ~2 JSON levels (object + "calls" array), the EVM allows up to MaxCallDepth=1024
        // (Yellow Paper / Nethermind.Evm.VirtualMachine.MaxCallDepth), plus a few levels of JSON-RPC
        // envelope. 4096 leaves comfortable headroom.
        public const int DefaultMaxDepth = 4096;
        private static readonly object _globalOptionsLock = new();

        private static readonly List<JsonConverter> _additionalConverters = [];
        private static readonly List<JsonTypeInfoResolverRegistration> _additionalResolvers = [];
        private static bool _strictHexFormat;
        private static int _optionsVersion;

        private readonly int _maxDepth;
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

        public object? Deserialize(string json, Type type) => JsonSerializer.Deserialize(json, type, GetSerializerOptions(indented: false));

        public T? Deserialize<T>(Stream stream) => JsonSerializer.Deserialize<T>(stream, GetSerializerOptions(indented: false));

        public T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, GetSerializerOptions(indented: false));

        public T? Deserialize<T>(ReadOnlySpan<byte> utf8Json) => JsonSerializer.Deserialize<T>(utf8Json, GetSerializerOptions(indented: false));

        public T? Deserialize<T>(ref Utf8JsonReader json) => JsonSerializer.Deserialize<T>(ref json, GetSerializerOptions(indented: false));

        public string Serialize<T>(T value, bool indented = false) => JsonSerializer.Serialize<T>(value, GetSerializerOptions(indented));

        private static JsonSerializerOptions CreateOptions(bool indented, bool strictQuantity = false, IEnumerable<JsonConverter>? instanceConverters = null, int maxDepth = DefaultMaxDepth)
        {
            SnapshotGlobalOptions(out bool strictHexFormat, out JsonConverter[] additionalConverters, out JsonTypeInfoResolverRegistration[] additionalResolvers);

            JsonSerializerOptions result = new()
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
                    new LongConverter(strictQuantity),
                    new UInt256Converter(strictQuantity),
                    new EvmWordConverter(),
                    new ULongConverter(strictQuantity),
                    new IntConverter(),
                    new ByteArrayConverter(),
                    new HexBytesConverter(),
                    new ByteArrayArrayConverter(),
                    new ByteReadOnlyMemoryConverter(),
                    new NullableByteReadOnlyMemoryConverter(),
                    new ArrayPoolListByteHexConverter(),
                    new NullableLongConverter(strictQuantity),
                    new NullableULongConverter(strictQuantity),
                    new NullableUInt256Converter(strictQuantity),
                    new NullableIntConverter(),
                    new TxTypeConverter(),
                    new DoubleConverter(),
                    new DoubleArrayConverter(),
                    new BooleanConverter(),
                    new AddressConverter(strictHexFormat),
                    new AddressAsKeyConverter(),
                    new MemoryByteConverter(),
                    new BigIntegerConverter(),
                    new NullableBigIntegerConverter(),
                    new JavaScriptObjectConverter(),
                    new PublicKeyHashedConverter(),
                    new PublicKeyConverter(),
                    new SignatureConverter(),
                    new ValueHash256Converter(strictHexFormat),
                    new Hash256Converter(strictHexFormat),
                    new Hash256ArrayConverter(),
                    new IPAddressConverter(),
                    new CappedArrayJsonConverter<int>(),
                    new CappedArrayByteJsonConverter(),
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

        /// <summary>
        /// Adds a JSON metadata resolver after first-party RPC resolvers and before the default reflection resolver.
        /// </summary>
        public static void AddTypeInfoResolver(IJsonTypeInfoResolver resolver) =>
            AddTypeInfoResolver(resolver, JsonTypeInfoResolverPriority.External);

        /// <summary>
        /// Adds a JSON metadata resolver with an explicit ordering priority.
        /// </summary>
        /// <remarks>
        /// Lower priority values are queried first. Re-registering the same resolver updates its priority while preserving
        /// its original registration sequence for stable tie-breaking.
        /// </remarks>
        public static void AddTypeInfoResolver(IJsonTypeInfoResolver resolver, JsonTypeInfoResolverPriority priority)
        {
            ArgumentNullException.ThrowIfNull(resolver);
            lock (_globalOptionsLock)
            {
                for (int i = 0; i < _additionalResolvers.Count; i++)
                {
                    JsonTypeInfoResolverRegistration existing = _additionalResolvers[i];
                    if (ReferenceEquals(existing.Resolver, resolver))
                    {
                        if (existing.Priority != priority)
                        {
                            _additionalResolvers[i] = existing.WithPriority(priority);
                            RefreshGlobalOptionsNoLock();
                        }

                        return;
                    }
                }

                _additionalResolvers.Add(new JsonTypeInfoResolverRegistration(resolver, priority, _additionalResolvers.Count));
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

        /// <summary>Options for RPC request parameter deserialization, enforcing EIP-1474 QUANTITY format when <see cref="StrictHexFormat"/> is <see langword="true"/>.</summary>
        public static JsonSerializerOptions JsonRpcRequestOptions { get; private set; } = CreateOptions(indented: false);

        private static readonly StreamPipeWriterOptions optionsLeaveOpen = new(pool: MemoryPool<byte>.Shared, minimumBufferSize: 16384, leaveOpen: true);
        private static readonly StreamPipeWriterOptions options = new(pool: MemoryPool<byte>.Shared, minimumBufferSize: 16384, leaveOpen: false);

        private static CountingStreamPipeWriter GetPipeWriter(Stream stream, bool leaveOpen) => new(stream, leaveOpen ? optionsLeaveOpen : options);

        public long Serialize<T>(Stream stream, T value, bool indented = false, bool leaveOpen = true)
        {
            CountingStreamPipeWriter countingWriter = GetPipeWriter(stream, leaveOpen);
            using Utf8JsonWriter writer = new(countingWriter, CreateWriterOptions(indented));
            JsonSerializer.Serialize(writer, value, GetSerializerOptions(indented));
            countingWriter.Complete();

            long outputCount = countingWriter.WrittenCount;
            return outputCount;
        }

        private JsonWriterOptions CreateWriterOptions(bool indented)
        {
            JsonWriterOptions writerOptions = new() { SkipValidation = true, Indented = indented, MaxDepth = _maxDepth };
            return writerOptions;
        }

        public async ValueTask<long> SerializeAsync<T>(Stream stream, T value, CancellationToken cancellationToken, bool indented = false, bool leaveOpen = true)
        {
            CountingStreamPipeWriter writer = GetPipeWriter(stream, leaveOpen);
            await JsonSerializer.SerializeAsync(writer, value, GetSerializerOptions(indented), cancellationToken);
            await writer.CompleteAsync();

            long outputCount = writer.WrittenCount;
            return outputCount;
        }

        public Task SerializeAsync<T>(PipeWriter writer, T value, bool indented = false)
        {
            using Utf8JsonWriter jsonWriter = new((IBufferWriter<byte>)writer, CreateWriterOptions(indented));
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

        public static void SerializeToStream<T>(Stream stream, T value, bool indented = false) => JsonSerializer.Serialize(stream, value, indented ? JsonOptionsIndented : JsonOptions);

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
            _jsonOptions = CreateOptions(indented: false, instanceConverters: _instanceConverters, maxDepth: _maxDepth);
            _jsonOptionsIndented = CreateOptions(indented: true, instanceConverters: _instanceConverters, maxDepth: _maxDepth);
            _instanceOptionsVersion = Volatile.Read(ref _optionsVersion);
        }

        private static void RefreshGlobalOptionsNoLock()
        {
            JsonOptions = CreateOptions(indented: false);
            JsonOptionsIndented = CreateOptions(indented: true);
            JsonRpcRequestOptions = CreateOptions(indented: false, strictQuantity: _strictHexFormat);
            Interlocked.Increment(ref _optionsVersion);
        }

        private static void SnapshotGlobalOptions(out bool strictHexFormat, out JsonConverter[] additionalConverters, out JsonTypeInfoResolverRegistration[] additionalResolvers)
        {
            lock (_globalOptionsLock)
            {
                strictHexFormat = _strictHexFormat;
                additionalConverters = new JsonConverter[_additionalConverters.Count];
                for (int i = 0; i < _additionalConverters.Count; i++)
                {
                    additionalConverters[i] = _additionalConverters[i];
                }

                additionalResolvers = new JsonTypeInfoResolverRegistration[_additionalResolvers.Count];
                for (int i = 0; i < _additionalResolvers.Count; i++)
                {
                    additionalResolvers[i] = _additionalResolvers[i];
                }

                SortResolverRegistrations(additionalResolvers);
            }
        }

        private static IJsonTypeInfoResolver BuildTypeInfoResolver(IReadOnlyList<JsonTypeInfoResolverRegistration> additionalResolvers)
        {
            int additionalResolversCount = additionalResolvers.Count;
            if (additionalResolversCount == 0)
            {
                return new DefaultJsonTypeInfoResolver();
            }

            IJsonTypeInfoResolver[] resolverChain = new IJsonTypeInfoResolver[additionalResolversCount + 1];
            for (int i = 0; i < additionalResolversCount; i++)
            {
                resolverChain[i] = additionalResolvers[i].Resolver;
            }

            resolverChain[additionalResolversCount] = new DefaultJsonTypeInfoResolver();
            return JsonTypeInfoResolver.Combine(resolverChain);
        }

        private static void SortResolverRegistrations(JsonTypeInfoResolverRegistration[] registrations) =>
            Array.Sort(registrations, static (left, right) =>
            {
                int priorityComparison = ((int)left.Priority).CompareTo((int)right.Priority);
                return priorityComparison != 0
                    ? priorityComparison
                    : left.Sequence.CompareTo(right.Sequence);
            });

        private static JsonConverter[] CopyConverters(IEnumerable<JsonConverter> converters)
        {
            ArgumentNullException.ThrowIfNull(converters);

            if (converters is JsonConverter[] convertersArray)
            {
                JsonConverter[] clone = new JsonConverter[convertersArray.Length];
                Array.Copy(convertersArray, clone, convertersArray.Length);
                return clone;
            }

            List<JsonConverter> list = [.. converters];

            return [.. list];
        }

        private readonly struct JsonTypeInfoResolverRegistration(IJsonTypeInfoResolver resolver, JsonTypeInfoResolverPriority priority, int sequence)
        {
            public IJsonTypeInfoResolver Resolver { get; } = resolver;

            public JsonTypeInfoResolverPriority Priority { get; } = priority;

            public int Sequence { get; } = sequence;

            public JsonTypeInfoResolverRegistration WithPriority(JsonTypeInfoResolverPriority priority) =>
                new(Resolver, priority, Sequence);
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
