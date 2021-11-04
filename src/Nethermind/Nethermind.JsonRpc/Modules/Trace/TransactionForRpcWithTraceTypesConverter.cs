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
using Nethermind.JsonRpc.Data;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class TransactionForRpcWithTraceTypesConverter : JsonConverter<TransactionForRpcWithTraceTypes>
    {
        public override void WriteJson(JsonWriter writer, TransactionForRpcWithTraceTypes value, JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }

        public override TransactionForRpcWithTraceTypes ReadJson(JsonReader reader, Type objectType,
            TransactionForRpcWithTraceTypes existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            existingValue ??= new();
            JArray jArray = JArray.Load(reader);
            existingValue.Transaction = serializer.Deserialize<TransactionForRpc>(jArray[0].CreateReader()) ?? throw new InvalidOperationException();
            existingValue.TraceTypes = serializer.Deserialize<string[]>(jArray[1].CreateReader()) ?? throw new InvalidOperationException();

            return existingValue;
        }
    }
}
