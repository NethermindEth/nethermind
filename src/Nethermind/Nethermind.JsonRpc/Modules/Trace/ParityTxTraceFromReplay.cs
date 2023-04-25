// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.ParityStyle;

namespace Nethermind.JsonRpc.Modules.Trace
{
    [JsonConverter(typeof(ParityTxTraceFromReplayJsonConverter))]
    public class ParityTxTraceFromReplay
    {
        public ParityTxTraceFromReplay()
        {
        }

        public ParityTxTraceFromReplay(ParityLikeTxTrace txTrace, bool includeTransactionHash = false)
        {
            Output = txTrace.Output;
            VmTrace = txTrace.VmTrace;
            Action = txTrace.Action;
            StateChanges = txTrace.StateChanges;
            TransactionHash = includeTransactionHash ? txTrace.TransactionHash : null;
        }

        public ParityTxTraceFromReplay(IReadOnlyCollection<ParityLikeTxTrace> txTraces, bool includeTransactionHash = false)
        {
            foreach (ParityLikeTxTrace txTrace in txTraces)
            {
                Output = txTrace.Output;
                VmTrace = txTrace.VmTrace;
                Action = txTrace.Action;
                StateChanges = txTrace.StateChanges;
                TransactionHash = includeTransactionHash ? txTrace.TransactionHash : null;
            }
        }

        public byte[]? Output { get; set; }

        public Keccak? TransactionHash { get; set; }

        public ParityVmTrace? VmTrace { get; set; }

        [JsonConverter(typeof(ParityTraceActionFromReplayJsonConverter))]
        public ParityTraceAction? Action { get; set; }

        public Dictionary<Address, ParityAccountStateChange>? StateChanges { get; set; }
    }

    public class ParityTraceActionFromReplayJsonConverter : JsonConverter<ParityTraceAction>
    {
        public override ParityTraceAction Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => throw new NotImplementedException();

        public override void Write(
            Utf8JsonWriter writer,
            ParityTraceAction value,
            JsonSerializerOptions options)
        {
            if (!value.IncludeInTrace)
            {
                return;
            }

            writer.WriteStartObject();

            writer.WritePropertyName("action"u8);
            JsonSerializer.Serialize(writer, value, options);

            if (value.Error is null)
            {
                writer.WritePropertyName("result"u8);
                JsonSerializer.Serialize(writer, value.Result, options);
            }
            else
            {
                writer.WritePropertyName("error"u8);
                JsonSerializer.Serialize(writer, value.Error, options);
            }

            writer.WriteNumber("subtraces"u8, value.Subtraces.Count(s => s.IncludeInTrace));

            writer.WritePropertyName("traceAddress"u8);
            if (value.TraceAddress is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                JsonSerializer.Serialize(writer, value.TraceAddress, options);
            }

            writer.WriteString("type"u8, value.Type);
            writer.WriteEndObject();
            foreach (ParityTraceAction subtrace in value.Subtraces)
            {
                writer.WriteStartObject();

                writer.WritePropertyName("action"u8);
                JsonSerializer.Serialize(writer, subtrace, options);

                writer.WritePropertyName("result"u8);
                JsonSerializer.Serialize(writer, subtrace.Result, options);

                writer.WritePropertyName("subtraces"u8);
                JsonSerializer.Serialize(writer, subtrace.Subtraces.Count, options);

                writer.WritePropertyName("traceAddress"u8);
                JsonSerializer.Serialize(writer, subtrace.TraceAddress, options);

                writer.WritePropertyName("type"u8);
                JsonSerializer.Serialize(writer, subtrace.Type, options);

                writer.WriteEndObject();
            }
        }
    }

    public class ParityTxTraceFromReplayJsonConverter : JsonConverter<ParityTxTraceFromReplay>
    {
        ParityTraceActionFromReplayJsonConverter _actionConverter = new();
        public override ParityTxTraceFromReplay Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => throw new NotImplementedException();

        public override void Write(
            Utf8JsonWriter writer,
            ParityTxTraceFromReplay value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            writer.WritePropertyName("output"u8);
            JsonSerializer.Serialize(writer, value.Output, options);

            writer.WritePropertyName("stateDiff"u8);
            if (value.StateChanges is not null)
            {
                writer.WriteStartObject();
                foreach ((Address address, ParityAccountStateChange stateChange) in value.StateChanges.OrderBy(sc => sc.Key, AddressComparer.Instance))
                {
                    writer.WritePropertyName(address.ToString());
                    JsonSerializer.Serialize(writer, stateChange, options);
                }

                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WritePropertyName("trace"u8);

            writer.WriteStartArray();
            if (value.Action is not null)
            {
                _actionConverter.Write(writer, value.Action, options);
            }
            writer.WriteEndArray();

            if (value.TransactionHash is not null)
            {
                writer.WritePropertyName("transactionHash"u8);
                JsonSerializer.Serialize(writer, value.TransactionHash, options);
            }

            writer.WritePropertyName("vmTrace"u8);
            JsonSerializer.Serialize(writer, value.VmTrace, options);

            writer.WriteEndObject();
        }
    }
}
