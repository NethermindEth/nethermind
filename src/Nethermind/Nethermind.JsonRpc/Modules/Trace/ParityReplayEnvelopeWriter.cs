// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text.Json;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Extensions;

namespace Nethermind.JsonRpc.Modules.Trace;

public static class ParityReplayEnvelopeWriter
{
    public static void WriteFromTrace(Utf8JsonWriter writer, ParityLikeTxTrace trace, bool includeTxHash, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("vmTrace"u8);
        JsonSerializer.Serialize(writer, trace.VmTrace, options);
        WriteTail(writer, trace, includeTxHash, options);
    }

    public static void WriteTail(Utf8JsonWriter writer, ParityLikeTxTrace trace, bool includeTxHash, JsonSerializerOptions options)
    {
        writer.WritePropertyName("output"u8);
        JsonSerializer.Serialize(writer, trace.Output, options);

        writer.WritePropertyName("stateDiff"u8);
        if (trace.StateChanges is not null)
        {
            writer.WriteStartObject();
            Span<byte> addressBytes = stackalloc byte[Address.Size * 2 + 2];
            addressBytes[0] = (byte)'0';
            addressBytes[1] = (byte)'x';
            Span<byte> hex = addressBytes[2..];
            foreach ((Address address, ParityAccountStateChange stateChange) in
                trace.StateChanges.OrderBy(static sc => sc.Key, GenericComparer.GetOptimized<Address>()))
            {
                address.Bytes.OutputBytesToByteHex(hex, false);
                writer.WritePropertyName(addressBytes);
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
        if (trace.Action is not null)
        {
            WriteActionRecursively(writer, trace.Action, options);
        }
        writer.WriteEndArray();

        if (includeTxHash)
        {
            writer.WritePropertyName("transactionHash"u8);
            JsonSerializer.Serialize(writer, trace.TransactionHash, options);
        }

        writer.WriteEndObject();
    }

    internal static void WriteTraceAddress(Utf8JsonWriter writer, in CappedArray<int> traceAddress)
    {
        if (traceAddress.IsNull)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartArray();
        ReadOnlySpan<int> span = traceAddress.AsSpan();
        for (int i = 0; i < span.Length; i++)
        {
            writer.WriteNumberValue(span[i]);
        }
        writer.WriteEndArray();
    }

    private static void WriteActionRecursively(Utf8JsonWriter writer, ParityTraceAction action, JsonSerializerOptions options)
    {
        if (!action.IncludeInTrace) return;

        writer.WriteStartObject();

        writer.WritePropertyName("action"u8);
        ParityTraceActionConverter.Instance.Write(writer, action, options);

        if (action.Error is null)
        {
            writer.WritePropertyName("result"u8);
            JsonSerializer.Serialize(writer, action.Result, options);
        }
        else
        {
            writer.WritePropertyName("error"u8);
            JsonSerializer.Serialize(writer, action.Error, options);
        }

        writer.WriteNumber("subtraces"u8, action.Subtraces.Count);

        writer.WritePropertyName("traceAddress"u8);
        WriteTraceAddress(writer, action.TraceAddress);

        writer.WriteString("type"u8, action.Type);
        writer.WriteEndObject();

        for (int i = 0; i < action.Subtraces.Count; i++)
        {
            WriteActionRecursively(writer, action.Subtraces[i], options);
        }
    }
}
