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

using System;
using System.Linq;
using Newtonsoft.Json;

namespace Nethermind.BeaconNode.OApiClient
{
    internal class PrefixedHexByteArrayNewtonsoftJsonConverter : JsonConverter
    {
        private const string Prefix = "0x";
            
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            byte[] bytes = (byte[]) value;
            string stringValue = Prefix + BitConverter.ToString(bytes).Replace("-", string.Empty);
            serializer.Serialize(writer, stringValue);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.String)
            {
                string hex = serializer.Deserialize<string>(reader);
                if (!string.IsNullOrEmpty(hex) && hex.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return Enumerable.Range(Prefix.Length, hex.Length - Prefix.Length)
                        .Where(x => x % 2 == 0)
                        .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                        .ToArray();
                }
            }
            return new byte[0];
        }

        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(byte[]);
        }
    }
}