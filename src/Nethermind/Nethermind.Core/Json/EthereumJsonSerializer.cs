/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.Core.Json
{
    public class EthereumJsonSerializer : IJsonSerializer
    {
        public EthereumJsonSerializer()
        {
            _serializer = JsonSerializer.Create(_settings);
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
        
        private static JsonSerializerSettings _settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None,
            Converters = BasicConverters
        };
        
        private static JsonSerializerSettings _readableSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented,
            Converters = ReadableConverters
        };
        
        public T DeserializeAnonymousType<T>(string json, T definition)
        {
            throw new NotSupportedException();
        }

        public T Deserialize<T>(string json)
        {
            return JsonConvert.DeserializeObject<T>(json, _settings);
        }

        private JsonSerializer _serializer;

        public (T Model, List<T> Collection) DeserializeObjectOrArray<T>(string json)
        {
            var token = JToken.Parse(json);
            if (token is JArray array)
            {
                foreach (var tokenElement in array)
                {
                    UpdateParams(tokenElement);
                }

                return (default, array.ToObject<List<T>>(_serializer));
            }
            
            UpdateParams(token);
            return (token.ToObject<T>(_serializer), null);
        }
        
        private void UpdateParams(JToken token)
        {
            var paramsToken = token.SelectToken("params");
            if (paramsToken == null)
            {
                paramsToken = token.SelectToken("Params");
                if (paramsToken == null)
                {
                    return;
                }

//                if (paramsToken == null)
//                {
//                    throw new FormatException("Missing 'params' token");
//                }
            }
            
            var values = new List<string>();
            foreach (var value in paramsToken.Value<IEnumerable<object>>())
            {
                var valueString = value?.ToString();
                if (valueString == null)
                {
                    values.Add($"\"null\"");
                    continue;
                }
                
                if (valueString.StartsWith("{") || valueString.StartsWith("["))
                {
                    values.Add(Serialize(valueString));
                    continue;
                }
                values.Add($"\"{valueString}\"");
            }

            var json = $"[{string.Join(",", values)}]";
            paramsToken.Replace(JToken.Parse(json));
        }

        public string Serialize<T>(T value, bool indented = false)
        {
            return JsonConvert.SerializeObject(value, indented ? _readableSettings : _settings);
        }

        public void RegisterConverter(JsonConverter converter)
        {
            BasicConverters.Add(converter);
            ReadableConverters.Add(converter);
            
            _readableSettings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = Formatting.Indented,
                Converters = ReadableConverters
            };
            
            _settings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = Formatting.None,
                Converters = BasicConverters
            };
        }
    }
}