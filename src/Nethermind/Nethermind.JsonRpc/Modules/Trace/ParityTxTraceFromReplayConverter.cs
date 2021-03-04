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
using System.Linq;
using Nethermind.Core;
using Nethermind.Evm.Tracing.ParityStyle;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class ParityTxTraceFromReplayConverter : JsonConverter<ParityTxTraceFromReplay>
    {
        private ParityTraceAddressConverter _traceAddressConverter = new();

        public override void WriteJson(JsonWriter writer, ParityTxTraceFromReplay value, JsonSerializer serializer)
        {
            writer.WriteStartObject();

            writer.WriteProperty("output", value.Output, serializer);
            writer.WritePropertyName("stateDiff");
            
            if (value.StateChanges != null)
            {
                writer.WriteStartObject();
                foreach ((Address address, ParityAccountStateChange stateChange) in value.StateChanges.OrderBy(sc => sc.Key, AddressComparer.Instance)) writer.WriteProperty(address.ToString(), stateChange, serializer);

                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull();
            }

            writer.WritePropertyName("trace");
            
            writer.WriteStartArray();
            if (value.Action != null)
            {
                WriteJson(writer, value.Action, serializer);
            }
            writer.WriteEndArray();

            if (value.TransactionHash != null)
            {
                writer.WriteProperty("transactionHash", value.TransactionHash, serializer);
            }
            
            writer.WriteProperty("vmTrace", value.VmTrace, serializer);

            writer.WriteEndObject();
        }

        // {
        //   "output": "0x",
        //   "stateDiff": null,
        //   "trace": [{
        //     "action": { ... },
        //     "result": {
        //       "gasUsed": "0x0",
        //       "output": "0x"
        //     },
        //     "subtraces": 0,
        //     "traceAddress": [],
        //     "type": "call"
        //   }],
        //   "vmTrace": null
        // }
        private void WriteJson(JsonWriter writer, ParityTraceAction traceAction, JsonSerializer serializer)
        {
            if (!traceAction.IncludeInTrace)
            {
                return;
            }

            writer.WriteStartObject();
            writer.WriteProperty("action", traceAction, serializer);
            if (traceAction.Error == null)
            {
                writer.WriteProperty("result", traceAction.Result, serializer);
            }
            else
            {
                writer.WriteProperty("error", traceAction.Error, serializer);
            }

            writer.WriteProperty("subtraces", traceAction.Subtraces.Count(s => s.IncludeInTrace));
            writer.WritePropertyName("traceAddress");
            _traceAddressConverter.WriteJson(writer, traceAction.TraceAddress, serializer);

            writer.WriteProperty("type", traceAction.Type);
            writer.WriteEndObject();
            foreach (ParityTraceAction subtrace in traceAction.Subtraces) WriteJson(writer, subtrace, serializer);
        }

        public override ParityTxTraceFromReplay ReadJson(JsonReader reader, Type objectType, ParityTxTraceFromReplay existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }
    }
}
