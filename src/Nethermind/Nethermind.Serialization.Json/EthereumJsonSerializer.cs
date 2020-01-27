//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Globalization;
using System.IO;
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

        public static IList<JsonConverter> BasicConverters { get; } = new List<JsonConverter>
        {
            new AddressConverter(),
            new KeccakConverter(),
            new BloomConverter(),
            new ByteArrayConverter(),
            new LongConverter(),
            new NullableLongConverter(),
            new UInt256Converter(),
            new NullableUInt256Converter(),
            new BigIntegerConverter(),
            new NullableBigIntegerConverter(),
            new PublicKeyConverter()
        };

        private static IList<JsonConverter> ReadableConverters { get; } = new List<JsonConverter>
        {
            new AddressConverter(),
            new KeccakConverter(),
            new BloomConverter(),
            new ByteArrayConverter(),
            new LongConverter(NumberConversion.Decimal),
            new NullableLongConverter(NumberConversion.Decimal),
            new UInt256Converter(NumberConversion.Decimal),
            new NullableUInt256Converter(NumberConversion.Decimal),
            new BigIntegerConverter(NumberConversion.Decimal),
            new NullableBigIntegerConverter(NumberConversion.Decimal),
            new PublicKeyConverter()
        };

        public T Deserialize<T>(string json)
        {
            using StringReader reader = new StringReader(json);
            using JsonReader jsonReader = new JsonTextReader(reader);
            return _internalSerializer.Deserialize<T>(jsonReader);
        }

        public string Serialize<T>(T value, bool indented = false)
        {
            StringWriter stringWriter = new StringWriter(new StringBuilder(256), CultureInfo.InvariantCulture);
            using (JsonTextWriter jsonTextWriter = new JsonTextWriter(stringWriter))
            {
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
            }
            
            return stringWriter.ToString();
        }
        
        public void Serialize<T>(Stream stream, T value, bool indented = false)
        {
            StreamWriter streamWriter = new StreamWriter(stream);
            using JsonTextWriter jsonTextWriter = new JsonTextWriter(streamWriter);
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
                Converters = BasicConverters
            };

            _internalSerializer = JsonSerializer.Create(_settings);
            _internalReadableSerializer = JsonSerializer.Create(_readableSettings);
        }
    }
}