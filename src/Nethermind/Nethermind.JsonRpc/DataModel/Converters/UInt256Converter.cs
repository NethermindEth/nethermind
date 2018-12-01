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
using System.Globalization;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.DataModel.Converters
{
    public class UInt256FullConverter : JsonConverter<UInt256>
    {
        public override void WriteJson(JsonWriter writer, UInt256 value, JsonSerializer serializer)
        {
            writer.WriteValue(string.Concat("0x", value.ToString("x64")));
        }

        public override UInt256 ReadJson(JsonReader reader, Type objectType, UInt256 existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            string s = (string)reader.Value;
            return UInt256.Parse(s.AsSpan(2), NumberStyles.AllowHexSpecifier);
        }
    }
    
    public class Bytes32Converter : JsonConverter<byte[]>
    {
        public override void WriteJson(JsonWriter writer, byte[] value, JsonSerializer serializer)
        {
            writer.WriteValue(string.Concat("0x", value.ToHexString(false).PadLeft(64, '0')));
        }

        public override byte[] ReadJson(JsonReader reader, Type objectType, byte[] existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            string s = (string) reader.Value;
            return Bytes.FromHexString(s);
        }
    }
    
    public class UInt256Converter : JsonConverter<UInt256>
    {
        public override void WriteJson(JsonWriter writer, UInt256 value, JsonSerializer serializer)
        {
            writer.WriteValue(string.Concat("0x", value.ToString("x")));
        }

        public override UInt256 ReadJson(JsonReader reader, Type objectType, UInt256 existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            string s = (string)reader.Value;
            return UInt256.Parse(s.AsSpan(2), NumberStyles.AllowHexSpecifier);
        }
    }
}