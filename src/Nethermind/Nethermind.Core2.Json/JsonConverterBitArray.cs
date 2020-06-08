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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Json
{
    public class JsonConverterBitArray : JsonConverter<BitArray>
    {
        public override BitArray Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            byte[] bytes = reader.GetBytesFromPrefixedHex();
            return new BitArray(bytes.Select(x => x != 0).ToArray());
        }

        public override void Write(Utf8JsonWriter writer, BitArray value, JsonSerializerOptions options)
        {
            byte[] bytes = value.Cast<bool>().Select(x => x ? (byte)0x01 : (byte)0x00).ToArray();
            writer.WritePrefixedHexStringValue(bytes);
        }
    }
}