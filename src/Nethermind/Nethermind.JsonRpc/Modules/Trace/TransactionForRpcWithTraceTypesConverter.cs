// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
