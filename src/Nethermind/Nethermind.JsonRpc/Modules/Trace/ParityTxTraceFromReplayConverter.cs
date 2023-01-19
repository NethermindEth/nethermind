// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

            if (value.StateChanges is not null)
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
            if (value.Action is not null)
            {
                WriteJson(writer, value.Action, serializer);
            }
            writer.WriteEndArray();

            if (value.TransactionHash is not null)
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
            if (traceAction.Error is null)
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
