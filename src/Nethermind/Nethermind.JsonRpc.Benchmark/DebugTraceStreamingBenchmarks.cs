// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Benchmark;

[MemoryDiagnoser]
public class DebugTraceStreamingBenchmarks
{
    [Params(1_000, 10_000, 100_000)]
    public int OpcodeCount { get; set; }

    [Benchmark(Baseline = true, Description = "Buffered: accumulate N entries, then serialize the full envelope")]
    public int Buffered()
    {
        List<GethTxTraceEntry> entries = new(OpcodeCount);
        for (int i = 0; i < OpcodeCount; i++) entries.Add(BuildEntry(i));

        GethLikeTxTrace trace = new()
        {
            Entries = entries,
            Gas = 21000,
            Failed = false,
            ReturnValue = Array.Empty<byte>(),
        };

        ArrayBufferWriter<byte> sink = new();
        using Utf8JsonWriter writer = new(sink);
        JsonSerializer.Serialize(writer, trace, EthereumJsonSerializer.JsonOptions);
        return sink.WrittenCount;
    }

    [Benchmark(Description = "Streaming: emit each entry to the writer as it's produced; previous entry is GC-eligible immediately")]
    public int Streaming()
    {
        ArrayBufferWriter<byte> sink = new();
        using Utf8JsonWriter writer = new(sink);
        writer.WriteStartObject();
        writer.WritePropertyName("structLogs"u8);
        writer.WriteStartArray();

        for (int i = 0; i < OpcodeCount; i++)
        {
            GethTxMemoryTraceEntry entry = BuildEntry(i);
            JsonSerializer.Serialize(writer, entry, EthereumJsonSerializer.JsonOptions);
        }

        writer.WriteEndArray();
        writer.WriteNumber("gas"u8, 21000);
        writer.WriteBoolean("failed"u8, false);
        writer.WritePropertyName("returnValue"u8);
        JsonSerializer.Serialize(writer, Array.Empty<byte>(), EthereumJsonSerializer.JsonOptions);
        writer.WriteEndObject();
        return sink.WrittenCount;
    }

    private static GethTxMemoryTraceEntry BuildEntry(int pc) => new()
    {
        ProgramCounter = pc,
        Opcode = "ADD",
        Depth = 1,
        Gas = 1_000_000 - pc,
        GasCost = 3,
        Memory = ["0x0000000000000000000000000000000000000000000000000000000000000000"],
        Stack = ["0x1", "0x2"],
        Error = null,
        Storage = new Dictionary<string, string>(),
    };
}
