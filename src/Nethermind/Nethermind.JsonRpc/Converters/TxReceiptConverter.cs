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
// 

using System;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Converters
{
    public class TxReceiptConverter : JsonConverter<TxReceipt>
    {
        public override void WriteJson(JsonWriter writer, TxReceipt value, JsonSerializer serializer)
        {
            serializer.Serialize(writer, new ReceiptForRpc(value.TxHash!, value, UInt256.Zero));
        }

        public override TxReceipt ReadJson(JsonReader reader, Type objectType, TxReceipt existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            return serializer.Deserialize<ReceiptForRpc>(reader)?.ToReceipt() ?? existingValue;
        }
    }
}
