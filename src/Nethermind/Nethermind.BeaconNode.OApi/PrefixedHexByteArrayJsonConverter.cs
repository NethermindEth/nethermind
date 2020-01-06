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
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core2;

namespace Nethermind.BeaconNode.OApi
{
//    internal class PrefixedHexByteArrayJsonConverter : JsonConverter<byte[]>
//    {
//        private const string Prefix = "0x";
//
//        public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
//        {
//            if (typeToConvert == typeof(string))
//            {
//                string hex = reader.GetString();
//                if (!string.IsNullOrEmpty(hex) && hex.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
//                {
//                    byte[] bytes = Bytes.FromHexString(hex);
//                    return bytes;
//                }
//            }
//            return new byte[0];
//        }
//
//        public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options)
//        {
//            string stringValue = Bytes.ToHexString(value, withZeroX: true);
//            writer.WriteStringValue(stringValue);
//        }
//    }
}
