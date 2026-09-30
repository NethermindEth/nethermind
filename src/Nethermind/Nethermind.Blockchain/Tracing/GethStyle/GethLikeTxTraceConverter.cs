// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Collections.Pooled;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle;

public class GethLikeTxTraceConverter : JsonConverter<GethLikeTxTrace>
{
    public override GethLikeTxTrace Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        // If not an object, it should be a custom tracer result which we can't deserialize properly
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Custom tracer result object is not supported. Expected start of object");
        }

        GethLikeTxTrace trace = new();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.ValueTextEquals("gas"u8))
            {
                reader.Read();
                NumberConversion previousValue = ForcedNumberConversion.Value;
                ForcedNumberConversion.Value = NumberConversion.Raw;
                try
                {
                    trace.Gas = JsonSerializer.Deserialize<ulong>(ref reader, options);
                }
                finally
                {
                    ForcedNumberConversion.Value = previousValue;
                }

                continue;
            }

            if (reader.ValueTextEquals("failed"u8))
            {
                reader.Read();
                trace.Failed = JsonSerializer.Deserialize<bool>(ref reader, options);
                continue;
            }

            if (reader.ValueTextEquals("returnValue"u8))
            {
                reader.Read();
                trace.ReturnValue = JsonSerializer.Deserialize<byte[]>(ref reader, options);
                continue;
            }

            if (reader.ValueTextEquals("structLogs"u8))
            {
                reader.Read();
                trace.Entries = JsonSerializer.Deserialize<List<GethTxTraceEntry>>(ref reader, options);
                continue;
            }

            // If we find any unexpected property, it should be a custom tracer result which we can't deserialize properly
            throw new JsonException($"Custom tracer result object is not supported. Unexpected property: {reader.GetString()}");
        }

        return trace;
    }

    public override void Write(
        Utf8JsonWriter writer,
        GethLikeTxTrace? value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.CustomTracerResult is not null)
        {
            JsonSerializer.Serialize(writer, value.CustomTracerResult, options);
            return;
        }

        writer.WriteStartObject();

        NumberConversion previousValue = ForcedNumberConversion.Value;
        ForcedNumberConversion.Value = NumberConversion.Raw;
        try
        {
            writer.WritePropertyName("gas"u8);
            JsonSerializer.Serialize(writer, value.Gas, options);
        }
        finally
        {
            ForcedNumberConversion.Value = previousValue;
        }

        writer.WritePropertyName("failed"u8);
        JsonSerializer.Serialize(writer, value.Failed, options);

        writer.WritePropertyName("returnValue"u8);
        JsonSerializer.Serialize(writer, value.ReturnValue, options);

        writer.WritePropertyName("structLogs"u8);
        WriteEntriesWithStorageForwardPass(writer, value.Entries);

        writer.WriteEndObject();
    }

    private static void WriteEntriesWithStorageForwardPass(
        Utf8JsonWriter writer,
        List<GethTxTraceEntry> entries)
    {
        using PooledDictionary<AddressAsKey, PooledDictionary<UInt256, UInt256>> runningByAddress = new(4);
        writer.WriteStartArray();
        try
        {
            foreach (GethTxTraceEntry entry in entries)
            {
                PooledDictionary<UInt256, UInt256>? map = null;
                if (entry.StorageDelta is { } delta)
                {
                    if (!runningByAddress.TryGetValue(delta.Address, out map))
                    {
                        map = new PooledDictionary<UInt256, UInt256>(8);
                        runningByAddress[delta.Address] = map;
                    }
                    map[delta.Key] = delta.Value;
                }
                WriteEntry(writer, entry, map);
            }
        }
        finally
        {
            foreach (PooledDictionary<UInt256, UInt256> inner in runningByAddress.Values)
                inner.Dispose();
        }
        writer.WriteEndArray();
    }

    private static readonly SearchValues<char> UnescapedCharacters = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 _");

    /// <summary>Counts compact opcode JSON bytes using the capture budget's default encoder.</summary>
    internal static long GetEntrySize(GethTxTraceEntry entry, int? storageCount)
    {
        int hexWordLength = "\"0x\""u8.Length + EvmPooledMemory.WordSize * 2;
        long size = "{\"pc\":"u8.Length + DecimalLength(entry.ProgramCounter)
            + ",\"op\":"u8.Length + StringLength(entry.Opcode)
            + ",\"gas\":"u8.Length + DecimalLength(entry.Gas)
            + ",\"gasCost\":"u8.Length + DecimalLength(entry.GasCost)
            + ",\"depth\":"u8.Length + DecimalLength((long)entry.Depth) + "}"u8.Length;
        if (entry.Error is not null) size += ",\"error\":"u8.Length + StringLength(entry.Error);
        if (entry.Refund is { } refund) size += ",\"refund\":"u8.Length + DecimalLength(refund);
        if (entry.Stack is { } stack)
        {
            size += ",\"stack\":[]"u8.Length;
            ReadOnlySpan<byte> bytes = stack.Span;
            for (int i = 0; i < bytes.Length; i += EvmStack.WordSize)
            {
                UInt256 word = new(bytes.Slice(i, EvmStack.WordSize), isBigEndian: true);
                size += "\"0x\""u8.Length + Math.Max(1, (256 - word.CountLeadingZeros() + 3) / 4);
            }
            size += Math.Max(0, bytes.Length / EvmStack.WordSize - 1);
        }
        if (entry.Memory is { IsEmpty: false } memory)
        {
            long words = memory.Length / EvmPooledMemory.WordSize;
            size += ",\"memory\":[]"u8.Length + words * hexWordLength + words - 1;
        }
        if ((storageCount ?? entry.Storage?.Count) is { } slots)
            size += ",\"storage\":{}"u8.Length + (long)slots * (hexWordLength * 2 + 1) + Math.Max(0, slots - 1);
        if (entry.ReturnData is not null) size += ",\"returnData\":"u8.Length + StringLength(entry.ReturnData);
        return size;
    }

    private static long StringLength(string? value) => value is null ? 4
        : value.AsSpan().IndexOfAnyExcept(UnescapedCharacters) < 0 ? (long)value.Length + 2
        : (long)JavaScriptEncoder.Default.Encode(value).Length + 2;

    private static int DecimalLength(long value) => value < 0
        ? 1 + DecimalLength((ulong)(-(value + 1)) + 1)
        : DecimalLength((ulong)value);

    private static int DecimalLength(ulong value)
    {
        int length = 1;
        while (value >= 10)
        {
            value /= 10;
            length++;
        }
        return length;
    }

    /// <remarks>Keep in sync with <see cref="GetEntrySize"/>; GethLikeTxTraceSizeTests verifies the size against this output.</remarks>
    internal static void WriteEntry(
        Utf8JsonWriter writer,
        GethTxTraceEntry entry,
        IDictionary<UInt256, UInt256>? storage)
    {
        writer.WriteStartObject();
        writer.WriteNumber("pc"u8, entry.ProgramCounter);
        writer.WriteString("op"u8, entry.Opcode);
        writer.WriteNumber("gas"u8, entry.Gas);
        writer.WriteNumber("gasCost"u8, entry.GasCost);
        writer.WriteNumber("depth"u8, entry.Depth);
        if (entry.Error is not null) writer.WriteString("error"u8, entry.Error);
        if (entry.Refund is { } refund) writer.WriteNumber("refund"u8, refund);

        if (entry.Stack is { } stack)
        {
            writer.WriteStartArray("stack"u8);
            ReadOnlySpan<byte> stackSpan = stack.Span;
            for (int i = 0; i < stackSpan.Length; i += EvmStack.WordSize)
                HexWriter.WriteUInt256HexRawValue(writer,
                    new UInt256(stackSpan.Slice(i, EvmStack.WordSize), isBigEndian: true));
            writer.WriteEndArray();
        }

        if (entry.Memory is { IsEmpty: false } memory)
        {
            writer.WriteStartArray("memory"u8);
            ReadOnlySpan<byte> memSpan = memory.Span;
            for (int i = 0; i < memSpan.Length; i += EvmPooledMemory.WordSize)
                HexWriter.WriteFixed32HexRawValue(writer,
                    memSpan.Slice(i, EvmPooledMemory.WordSize), addHexPrefix: true);
            writer.WriteEndArray();
        }

        IDictionary<UInt256, UInt256>? effectiveStorage = storage ?? entry.Storage;
        if (effectiveStorage is not null)
        {
            writer.WriteStartObject("storage"u8);
            foreach (KeyValuePair<UInt256, UInt256> kv in effectiveStorage)
                HexWriter.WriteUInt256StorageSlot(writer, kv.Key, kv.Value);
            writer.WriteEndObject();
        }

        if (entry.ReturnData is not null) writer.WriteString("returnData"u8, entry.ReturnData);
        writer.WriteEndObject();
    }
}
