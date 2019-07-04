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
using System.Linq;
using Nethermind.Core;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Tracing;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class ParityLikeTxTraceConverter : JsonConverter<ParityLikeTxTrace>
    {
        private ParityTraceAddressConverter _traceAddressConverter = new ParityTraceAddressConverter();

        public override void WriteJson(JsonWriter writer, ParityLikeTxTrace value, JsonSerializer serializer)
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

        /*
         *    "action": {
         *      "callType": "call",
         *      "from": "0x430adc807210dab17ce7538aecd4040979a45137",
         *      "gas": "0x1a1f8",
         *      "input": "0x",
         *      "to": "0x9bcb0733c56b1d8f0c7c4310949e00485cae4e9d",
         *      "value": "0x2707377c7552d8000"
         *    },
         *    "blockHash": "0x3aa472d57e220458fe5b9f1587b9211de68b27504064f5f6e427c68fc1691a29",
         *    "blockNumber": 2392500,
         *    "result": {
         *      "gasUsed": "0x2162",
         *      "output": "0x"
         *    },
         *    "subtraces": 2,
         *    "traceAddress": [],
         *    "transactionHash": "0x847ed5e2e9430bc6ee925a81137ebebe0cea1352209f96723d3503eb7a707aa8",
         *    "transactionPosition": 42,
         *    "type": "call"
         */
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

        public override ParityLikeTxTrace ReadJson(JsonReader reader, Type objectType, ParityLikeTxTrace existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }
    }
}