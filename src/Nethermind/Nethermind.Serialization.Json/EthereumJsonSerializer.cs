// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Nethermind.Core.Collections;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Nethermind.Serialization.Json
{
    public class EthereumJsonSerializer : IJsonSerializer
    {
        private readonly int? _maxDepth;
        private JsonSerializer _internalSerializer;
        private JsonSerializer _internalReadableSerializer;

        private JsonSerializerSettings _settings;
        private JsonSerializerSettings _readableSettings;

        public EthereumJsonSerializer(int? maxDepth = null, params JsonConverter[] converters)
        {
            _maxDepth = maxDepth;
            BasicConverters.AddRange(converters);
            ReadableConverters.AddRange(converters);
            RebuildSerializers(maxDepth);
        }

        public static IReadOnlyList<JsonConverter> CommonConverters { get; } = new ReadOnlyCollection<JsonConverter>(
            new List<JsonConverter>
            {
                new AddressConverter(),
                new KeccakConverter(),
                new BloomConverter(),
                new ByteArrayConverter(),
                new LongConverter(),
                new ULongConverter(),
                new NullableLongConverter(),
                new NullableULongConverter(),
                new UInt256Converter(),
                new NullableUInt256Converter(),
                new BigIntegerConverter(),
                new NullableBigIntegerConverter(),
                new PublicKeyConverter(),
                new TxTypeConverter()
            });

        public IList<JsonConverter> BasicConverters { get; } = CommonConverters.ToList();

        private IList<JsonConverter> ReadableConverters { get; } = new List<JsonConverter>
        {
            new AddressConverter(),
            new KeccakConverter(),
            new BloomConverter(),
            new ByteArrayConverter(),
            new LongConverter(NumberConversion.Decimal),
            new ULongConverter(NumberConversion.Decimal),
            new NullableLongConverter(NumberConversion.Decimal),
            new NullableULongConverter(NumberConversion.Decimal),
            new UInt256Converter(NumberConversion.Decimal),
            new NullableUInt256Converter(NumberConversion.Decimal),
            new BigIntegerConverter(NumberConversion.Decimal),
            new NullableBigIntegerConverter(NumberConversion.Decimal),
            new PublicKeyConverter(),
            new TxTypeConverter()
        };

        public T Deserialize<T>(Stream stream)
        {
            using StreamReader reader = new(stream);
            return Deserialize<T>(reader);
        }

        public T Deserialize<T>(string json)
        {
            using StringReader reader = new(json);
            return Deserialize<T>(reader);
        }

        private T Deserialize<T>(TextReader reader)
        {
            using JsonReader jsonReader = new JsonTextReader(reader);
            return _internalSerializer.Deserialize<T>(jsonReader);
        }


        public string Serialize<T>(T value, bool indented = false)
        {
            StringWriter stringWriter = new(new StringBuilder(256), CultureInfo.InvariantCulture);
            using JsonTextWriter jsonTextWriter = new(stringWriter);
            if (indented)
            {
                jsonTextWriter.Formatting = _internalReadableSerializer.Formatting;
                _internalReadableSerializer.Serialize(jsonTextWriter, value, typeof(T));
            }
            else
            {
                jsonTextWriter.Formatting = _internalSerializer.Formatting;
                _internalSerializer.Serialize(jsonTextWriter, value, typeof(T));
            }

            return stringWriter.ToString();
        }

        static System.Text.Json.JsonSerializerOptions jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = false,
            IncludeFields = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            Converters =
            {
                new AddressJsonConverter(),
                new LongJsonConverter(),
                new UInt256JsonConverter(),
                new ULongJsonConverter(),
                new BloomJsonConverter(),
                new ByteArrayJsonConverter(),
                new KeccakJsonConverter(),
                new NullableLongJsonConverter(),
                new NullableULongJsonConverter(),
                new NullableUInt256JsonConverter(),
                new PublicKeyJsonConverter(),
                new TxTypeJsonConverter(),
                new DoubleConverter(),
                new DictionaryAddressKeyConverter()
            }
        };

        static System.Text.Json.JsonSerializerOptions jsonOptionsIndented = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            IncludeFields = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            Converters =
            {
                new AddressJsonConverter(),
                new LongJsonConverter(),
                new UInt256JsonConverter(),
                new ULongJsonConverter(),
                new BloomJsonConverter(),
                new ByteArrayJsonConverter(),
                new KeccakJsonConverter(),
                new NullableLongJsonConverter(),
                new NullableULongJsonConverter(),
                new NullableUInt256JsonConverter(),
                new PublicKeyJsonConverter(),
                new TxTypeJsonConverter(),
                new DoubleConverter(),
                new DictionaryAddressKeyConverter()
            }
        };

        [ThreadStatic]
        private static CountingStream _countingStream;
        private static CountingStream GetStream(Stream stream)
        {
            CountingStream countingStream = (_countingStream ??= new CountingStream());
            countingStream.Set(stream);
            return countingStream;
        }

        public long Serialize<T>(Stream stream, T value, bool indented = false)
        {
            CountingStream countingStream = GetStream(stream);
            System.Text.Json.JsonSerializer.Serialize(countingStream, value, indented ? jsonOptionsIndented : jsonOptions);
            long position = countingStream.Position;
            countingStream.Reset();
            return position;
        }

        public void RegisterConverter(JsonConverter converter)
        {
            BasicConverters.Add(converter);
            ReadableConverters.Add(converter);

            RebuildSerializers(_maxDepth);
        }

        private void RebuildSerializers(int? maxDepth = null)
        {
            _readableSettings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = Formatting.Indented,
                Converters = ReadableConverters,
            };

            _settings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = Formatting.None,
                Converters = BasicConverters,
            };

            if (maxDepth is not null)
            {
                _readableSettings.MaxDepth = _settings.MaxDepth = maxDepth.Value;
            }

            _internalSerializer = JsonSerializer.Create(_settings);
            _internalReadableSerializer = JsonSerializer.Create(_readableSettings);
        }

        private sealed class CountingStream : Stream
        {
            private Stream _wrappedStream;
            private long _position;

            public void Set(Stream stream)
            {
                _position = 0;
                _wrappedStream = stream;
            }

            public void Reset()
            {
                _position = 0;
                _wrappedStream = null;
            }

            public override long Position { get => _position; set => throw new NotSupportedException(); }

            public override void Flush() => _wrappedStream.Flush();

            public override void Write(byte[] buffer, int offset, int count)
            {
                _position += count;
                _wrappedStream.Write(buffer, offset, count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                _position += buffer.Length;
                _wrappedStream.Write(buffer);
            }

            public override bool CanRead => _wrappedStream.CanRead;
            public override bool CanSeek => _wrappedStream.CanSeek;
            public override bool CanWrite => _wrappedStream.CanWrite;
            public override long Length => _wrappedStream.Length;
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
