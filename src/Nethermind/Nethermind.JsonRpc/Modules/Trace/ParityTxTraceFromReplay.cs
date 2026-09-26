// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Blockchain.Tracing.ParityStyle;

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

        public virtual byte[]? Output { get; set; }

        public virtual Hash256? TransactionHash { get; set; }

        public virtual ParityVmTrace? VmTrace { get; set; }

        [JsonConverter(typeof(ParityTraceActionFromReplayJsonConverter))]
        public virtual ParityTraceAction? Action { get; set; }

        [JsonPropertyName("stateDiff")]
        public virtual Dictionary<Address, ParityAccountStateChange>? StateChanges { get; set; }
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
            ParityTraceActionConverter.Instance.Write(writer, value, options);

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

            writer.WriteNumber("subtraces"u8, value.Subtraces.Count);

            writer.WritePropertyName("traceAddress"u8);
            ParityReplayEnvelopeWriter.WriteTraceAddress(writer, value.TraceAddress);

            writer.WriteString("type"u8, value.Type);
            writer.WriteEndObject();

            foreach (ParityTraceAction subtrace in value.Subtraces)
            {
                Write(writer, subtrace, options);
            }
        }
    }

    public class ParityTxTraceFromReplayJsonConverter : JsonConverter<ParityTxTraceFromReplay>
    {
        readonly ParityTraceActionFromReplayJsonConverter _actionConverter = new();
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
            ParityReplayEnvelopeWriter.WriteStateDiff(writer, value.StateChanges, options);

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
