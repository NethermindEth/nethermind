// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Modules.Trace;

public static class ParityReplayEnvelopeWriter
{
    private static readonly SerializedTraces.Pool BufferedValues = new(8, 16);

    public static void WriteFromTrace(Utf8JsonWriter writer, ParityLikeTxTrace trace, bool includeTxHash, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("vmTrace"u8);
        TypeInfoJsonSerializer.Serialize(writer, trace.VmTrace, options);
        WriteTail(writer, trace, includeTxHash, options);
    }

    public static void WriteTail(Utf8JsonWriter writer, ParityLikeTxTrace trace, bool includeTxHash, JsonSerializerOptions options)
    {
        TraceStreamOutput output = new(writer, null, default, 0);
        WriteTail(ref output, trace, includeTxHash, options, null);
    }

    internal static void WriteTail(ref TraceStreamOutput output, ParityLikeTxTrace trace, bool includeTxHash,
        JsonSerializerOptions options, TraceOutputLimit? limit)
    {
        BufferedValue? buffer = limit is not null && ParallelTraceStream.SerializeAheadOptions(output.Writer) is { } valueOptions
            ? new(BufferedValues.Rent(valueOptions), valueOptions, limit)
            : null;
        try
        {
            WriteTailValues(ref output, trace, includeTxHash, options, buffer);
        }
        finally
        {
            if (buffer is { } staged) BufferedValues.Return(staged.Traces);
        }
    }

    private static void WriteTailValues(ref TraceStreamOutput output, ParityLikeTxTrace trace, bool includeTxHash,
        JsonSerializerOptions options, BufferedValue? buffer)
    {
        Utf8JsonWriter writer = output.Writer;
        writer.WritePropertyName("output"u8);
        WriteValue(ref output, trace.Output, options, buffer);

        writer.WritePropertyName("stateDiff"u8);
        WriteStateDiff(ref output, trace.StateChanges, options, buffer);

        writer.WritePropertyName("trace"u8);
        writer.WriteStartArray();
        if (trace.Action is not null)
        {
            WriteActionRecursively(ref output, trace.Action, options, buffer);
        }
        writer.WriteEndArray();

        if (includeTxHash)
        {
            writer.WritePropertyName("transactionHash"u8);
            WriteValue(ref output, trace.TransactionHash, options, buffer);
        }

        writer.WriteEndObject();
    }

    private static void WriteValue<T>(ref TraceStreamOutput output, T value, JsonSerializerOptions options, BufferedValue? buffer)
    {
        Utf8JsonWriter writer = buffer?.Start(ref output) ?? output.Writer;
        TypeInfoJsonSerializer.Serialize(writer, value, options);
        buffer?.Commit(ref output, writer);
    }

    /// <summary>Writes the accounts in ascending address order, sorting them in a pooled buffer.</summary>
    internal static void WriteStateDiff(Utf8JsonWriter writer, Dictionary<Address, ParityAccountStateChange>? stateChanges, JsonSerializerOptions options)
    {
        TraceStreamOutput output = new(writer, null, default, 0);
        WriteStateDiff(ref output, stateChanges, options, null);
    }

    [SkipLocalsInit]
    private static void WriteStateDiff(ref TraceStreamOutput output, Dictionary<Address, ParityAccountStateChange>? stateChanges,
        JsonSerializerOptions options, BufferedValue? buffer)
    {
        Utf8JsonWriter writer = output.Writer;
        if (stateChanges is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        Span<byte> addressBytes = stackalloc byte[Address.Size * 2 + 2];
        addressBytes[0] = (byte)'0';
        addressBytes[1] = (byte)'x';
        Span<byte> hex = addressBytes[2..];

        using ArrayPoolListRef<KeyValuePair<Address, ParityAccountStateChange>> sorted = new(stateChanges.Count, stateChanges);
        sorted.Sort(static (x, y) => x.Key.CompareTo(y.Key));
        foreach ((Address address, ParityAccountStateChange stateChange) in sorted.AsSpan())
        {
            address.Bytes.OutputBytesToByteHex(hex, false);
            writer.WritePropertyName(addressBytes);
            WriteValue(ref output, stateChange, options, buffer);
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

    private static void WriteActionRecursively(ref TraceStreamOutput output, ParityTraceAction action,
        JsonSerializerOptions options, BufferedValue? buffer)
    {
        if (!action.IncludeInTrace) return;

        Utf8JsonWriter writer = buffer?.Start(ref output) ?? output.Writer;
        writer.WriteStartObject();

        writer.WritePropertyName("action"u8);
        ParityTraceActionConverter.Instance.Write(writer, action, options);

        writer.WritePropertyName("result"u8);
        TypeInfoJsonSerializer.Serialize(writer, action.Result, options);

        if (action.Error is not null)
        {
            writer.WritePropertyName("error"u8);
            TypeInfoJsonSerializer.Serialize(writer, action.Error, options);
        }

        writer.WriteNumber("subtraces"u8, action.Subtraces.Count);

        writer.WritePropertyName("traceAddress"u8);
        WriteTraceAddress(writer, action.TraceAddress);

        writer.WriteString("type"u8, action.Type);
        writer.WriteEndObject();
        buffer?.Commit(ref output, writer);

        for (int i = 0; i < action.Subtraces.Count; i++)
        {
            WriteActionRecursively(ref output, action.Subtraces[i], options, buffer);
        }
    }

    private readonly record struct BufferedValue(SerializedTraces Traces, JsonWriterOptions Options, TraceOutputLimit Limit)
    {
        public Utf8JsonWriter Start(ref TraceStreamOutput output)
        {
            Utf8JsonWriter writer = output.Writer;
            Limit.BypassedBytes = output.BypassedBytes;
            return Traces.Start(Options, Limit, writer.BytesCommitted + writer.BytesPending + 1);
        }

        public void Commit(ref TraceStreamOutput output, Utf8JsonWriter writer)
        {
            writer.Flush();
            Traces.Seal();
            output.WriteSerialized(Traces.Written);
            Traces.Empty();
        }
    }
}
