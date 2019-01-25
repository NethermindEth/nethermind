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
        public static IList<JsonConverter> BasicConverters { get; } = new JsonConverter[]
        {
            new AddressConverter(),
            new KeccakConverter(),
            new BloomConverter(),
            new ByteArrayConverter(),
            new UInt256Converter(),
            new NullableUInt256Converter(),
            new BigIntegerConverter(),
            new NullableBigIntegerConverter()
        };
        
        private static IList<JsonConverter> ReadableConverters { get; } = new JsonConverter[]
        {
            new AddressConverter(),
            new KeccakConverter(),
            new BloomConverter(),
            new ByteArrayConverter(),
            new UInt256Converter(NumberConversion.Decimal),
            new NullableUInt256Converter(NumberConversion.Decimal),
            new BigIntegerConverter(NumberConversion.Decimal),
            new NullableBigIntegerConverter(NumberConversion.Decimal)
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
            Formatting = Formatting.None,
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

        public (T Model, IEnumerable<T> Collection) DeserializeObjectOrArray<T>(string json)
        {
            throw new NotSupportedException();
        }

        public string Serialize<T>(T value, bool indented = false)
        {
            return JsonConvert.SerializeObject(value, indented ? _readableSettings : _settings);
        }
    }
}