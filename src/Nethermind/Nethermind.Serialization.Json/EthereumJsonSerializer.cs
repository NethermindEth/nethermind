// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Serialization.Json
{
    public class EthereumJsonSerializer : IJsonSerializer
    {
        private int? _maxDepth;
        public EthereumJsonSerializer(int? maxDepth = null)
        {
            _maxDepth = maxDepth;
        }

        public T Deserialize<T>(Stream stream)
        {
            return JsonSerializer.Deserialize<T>(stream, JsonOptions);
        }

        public T Deserialize<T>(string json)
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }

        public string Serialize<T>(T value, bool indented = false)
        {
            return JsonSerializer.Serialize<T>(value, indented ? JsonOptionsIndented : JsonOptions);
        }

        private static JsonSerializerOptions CreateOptions(bool indented)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = false,
                IncludeFields = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters =
                {
                    new LongConverter(),
                    new UInt256Converter(),
                    new ULongConverter(),
                    new IntConverter(),
                    new ByteArrayConverter(),
                    new NullableLongConverter(),
                    new NullableULongConverter(),
                    new NullableUInt256Converter(),
                    new NullableIntConverter(),
                    new TxTypeConverter(),
                    new DoubleConverter(),
                    new DoubleArrayConverter(),
                    new BooleanConverter(),
                    new DictionaryAddressKeyConverter()
                }
            };

            foreach (var converter in _additionalConverters)
            {
                options.Converters.Add(converter);
            }

            return options;
        }

        private static List<JsonConverter> _additionalConverters = new();
        public static void AddConverter(JsonConverter converter)
        {
            _additionalConverters.Add(converter);

            JsonOptions = CreateOptions(indented: false);
            JsonOptionsIndented = CreateOptions(indented: true);
        }

        public static JsonSerializerOptions JsonOptions { get; private set; } = new JsonSerializerOptions
        {
            WriteIndented = false,
            IncludeFields = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters =
            {
                new LongConverter(),
                new UInt256Converter(),
                new ULongConverter(),
                new IntConverter(),
                new ByteArrayConverter(),
                new NullableLongConverter(),
                new NullableULongConverter(),
                new NullableUInt256Converter(),
                new NullableIntConverter(),
                new TxTypeConverter(),
                new DoubleConverter(),
                new DoubleArrayConverter(),
                new BooleanConverter(),
                new DictionaryAddressKeyConverter()
            }
        };

        public static JsonSerializerOptions JsonOptionsIndented { get; private set; } = new JsonSerializerOptions
        {
            WriteIndented = true,
            IncludeFields = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters =
            {
                new LongConverter(),
                new UInt256Converter(),
                new ULongConverter(),
                new IntConverter(),
                new ByteArrayConverter(),
                new NullableLongConverter(),
                new NullableULongConverter(),
                new NullableUInt256Converter(),
                new NullableIntConverter(),
                new TxTypeConverter(),
                new DoubleConverter(),
                new DoubleArrayConverter(),
                new BooleanConverter(),
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
            JsonSerializer.Serialize(countingStream, value, indented ? JsonOptionsIndented : JsonOptions);
            long position = countingStream.Position;
            countingStream.Reset();
            return position;
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
