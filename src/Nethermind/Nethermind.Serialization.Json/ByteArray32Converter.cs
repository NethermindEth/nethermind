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

using System;
using Nethermind.Core.Extensions;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json
{
    public class Bytes32Converter : JsonConverter<byte[]>
    {
        public override void WriteJson(JsonWriter writer, byte[] value, JsonSerializer serializer)
        {
            writer.WriteValue(string.Concat("0x", value.ToHexString(false).PadLeft(64, '0')));
        }

        public override byte[] ReadJson(
            JsonReader reader,
            Type objectType,
            byte[] existingValue,
            bool hasExistingValue,
            JsonSerializer serializer)
        {
            string s = (string) reader.Value;
            if (s is null)
            {
                return null;
            }
            
            return Bytes.FromHexString(s);
        }
    }
}
