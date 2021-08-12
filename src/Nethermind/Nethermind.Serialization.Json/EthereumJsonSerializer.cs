//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Nethermind.Serialization.Json
{
    public class EthereumJsonSerializer : IJsonSerializer
    {
        private JsonSerializer _internalSerializer;
        private JsonSerializer _internalReadableSerializer;

        private JsonSerializerSettings _settings;
        private JsonSerializerSettings _readableSettings;

        public EthereumJsonSerializer()
        {
            RebuildSerializers();
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

        public long Serialize<T>(Stream stream, T value, bool indented = false)
        {
            using StreamWriter streamWriter = new(stream, leaveOpen: true);
            using CountingTextWriter countingTextWriter = new(streamWriter);
            using JsonTextWriter jsonTextWriter = new(countingTextWriter);
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

            return countingTextWriter.Size;
        }

        public void RegisterConverter(JsonConverter converter)
        {
            BasicConverters.Add(converter);
            ReadableConverters.Add(converter);

            RebuildSerializers();
        }

        private void RebuildSerializers()
        {
            _readableSettings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = Formatting.Indented,
                Converters = ReadableConverters
            };

            _settings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = Formatting.None,
                Converters = BasicConverters,
            };

            _internalSerializer = JsonSerializer.Create(_settings);
            _internalReadableSerializer = JsonSerializer.Create(_readableSettings);
        }
    }
}
