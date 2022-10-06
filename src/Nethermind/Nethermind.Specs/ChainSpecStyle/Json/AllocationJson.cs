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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;

namespace Nethermind.Specs.ChainSpecStyle.Json
{
    internal class AllocationJson
    {
        public BuiltInJson BuiltIn { get; set; }

        public BalanceJson Balance { get; set; }

        public UInt256 Nonce { get; set; }

        public byte[] Code { get; set; }

        public byte[] Constructor { get; set; }
        public Dictionary<string, string> Storage { get; set; }

        public Dictionary<UInt256, byte[]> GetConvertedStorage()
        {
            return Storage?.ToDictionary(s => Bytes.FromHexString(s.Key).ToUInt256(), s => Bytes.FromHexString(s.Value));
        }

        internal class BalanceJson : SortedDictionary<long, UInt256> { }

        internal class BalanceJsonConverter : JsonConverter<BalanceJson>
        {
            public override void WriteJson(JsonWriter writer, BalanceJson value, JsonSerializer serializer)
            {
                throw new NotImplementedException();
            }

            public override BalanceJson ReadJson(JsonReader reader, Type objectType, BalanceJson existingValue, bool hasExistingValue, JsonSerializer serializer)
            {
                existingValue ??= new BalanceJson();
                if (reader.TokenType == JsonToken.String || reader.TokenType == JsonToken.Integer)
                {
                    existingValue.Add(0, serializer.Deserialize<UInt256>(reader));
                }
                else
                {
                    var balances = serializer.Deserialize<Dictionary<string, UInt256>>(reader)
                                   ?? throw new ArgumentException("Cannot deserialize balances.");
                    foreach (var balance in balances)
                    {
                        existingValue.Add(LongConverter.FromString(balance.Key), balance.Value);
                    }
                }

                return existingValue;
            }
        }
    }
}
