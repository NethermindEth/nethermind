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
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Newtonsoft.Json;

namespace Nethermind.Core.Json
{
    public class KeccakConverter : JsonConverter<Keccak>
    {
        public override void WriteJson(JsonWriter writer, Keccak value, Newtonsoft.Json.JsonSerializer serializer)
        {
            if (value == null)
            {
                writer.WriteNull();
            }
            else
            {
                writer.WriteValue(Bytes.ByteArrayToHexViaLookup32Safe(value.Bytes, true));
            }
        }

        public override Keccak ReadJson(JsonReader reader, Type objectType, Keccak existingValue, bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer)
        {
            string s = (string) reader.Value;
            return s == null ? null : new Keccak(Bytes.FromHexString(s));
        }
    }
}