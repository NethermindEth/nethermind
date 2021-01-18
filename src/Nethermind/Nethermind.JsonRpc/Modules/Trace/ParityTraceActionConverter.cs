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
using Nethermind.Evm.Tracing.ParityStyle;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Trace
{
    /*
     * {
     *   "callType": "call",
     *   "from": "0x430adc807210dab17ce7538aecd4040979a45137",
     *   "gas": "0x1a1f8",
     *   "input": "0x",
     *   "to": "0x9bcb0733c56b1d8f0c7c4310949e00485cae4e9d",
     *    "value": "0x2707377c7552d8000"
     * },
     */
    public class ParityTraceActionConverter : JsonConverter<ParityTraceAction>
    {
        public override void WriteJson(JsonWriter writer, ParityTraceAction value, JsonSerializer serializer)
        {
            if (value.Type == "reward")
            {
                WriteRewardJson(writer, value, serializer);
                return;
            }
            
            if (value.Type == "suicide")
            {
                WriteSelfDestructJson(writer, value, serializer);
                return;
            }
            
            writer.WriteStartObject();
            if (value.CallType != "create")
            {
                writer.WriteProperty("callType", value.CallType);
            }
            else
            {
                writer.WriteProperty("creationMethod", value.CreationMethod);
            }

            writer.WriteProperty("from", value.From, serializer);
            writer.WriteProperty("gas", value.Gas, serializer);

            if (value.CallType == "create")
            {
                writer.WriteProperty("init", value.Input, serializer);
            }
            else
            {
                writer.WriteProperty("input", value.Input, serializer);
                writer.WriteProperty("to", value.To, serializer);    
            }
            
            writer.WriteProperty("value", value.Value, serializer);
            writer.WriteEndObject();
        }

        private void WriteSelfDestructJson(JsonWriter writer, ParityTraceAction value, JsonSerializer serializer)
        {
            writer.WriteStartObject();
            writer.WriteProperty("address", value.From, serializer);
            writer.WriteProperty("balance", value.Value, serializer);
            writer.WriteProperty("refundAddress", value.To, serializer);
            writer.WriteEndObject();
        }
        
        private void WriteRewardJson(JsonWriter writer, ParityTraceAction value, JsonSerializer serializer)
        {
            writer.WriteStartObject();
            writer.WriteProperty("author", value.Author, serializer);
            writer.WriteProperty("rewardType", value.RewardType, serializer);
            writer.WriteProperty("value", value.Value, serializer);
            writer.WriteEndObject();
        }

        public override ParityTraceAction ReadJson(JsonReader reader, Type objectType, ParityTraceAction existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }
    }
}
