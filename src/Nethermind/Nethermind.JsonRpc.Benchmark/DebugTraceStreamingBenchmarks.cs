// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Benchmark;

[MemoryDiagnoser]
public class DebugTraceStreamingBenchmarks
{
    [Params(1_000, 10_000, 100_000)]
    public int OpcodeCount { get; set; }

    private Block _block = null!;
    private Transaction _tx = null!;
    private ExecutionEnvironment _env;

    [GlobalSetup]
    public void Setup()
    {
        _tx = Build.A.Transaction.WithTo(TestItem.AddressA).TestObject;
        _block = Build.A.Block.WithTransactions(_tx).TestObject;
        _env = ExecutionEnvironment.Rent(CodeInfo.Empty, Address.Zero, Address.Zero, null, 1, default, default);
    }

    [Benchmark(Baseline = true, Description = "Throughput: buffered — accumulate N entries, then serialize the full envelope")]
    public int Throughput_Buffered()
    {
        List<GethTxTraceEntry> entries = new(OpcodeCount);
        for (int i = 0; i < OpcodeCount; i++) entries.Add(BuildEntry(i));

        GethLikeTxTrace trace = new() { Entries = entries, Gas = 21000, ReturnValue = [] };

        ArrayBufferWriter<byte> sink = new();
        using Utf8JsonWriter writer = new(sink);
        JsonSerializer.Serialize(writer, trace, EthereumJsonSerializer.JsonOptions);
        return sink.WrittenCount;
    }

    [Benchmark(Description = "Throughput: streaming — emit each entry inline via direct Utf8JsonWriter writes")]
    public int Throughput_Streaming()
    {
        ArrayBufferWriter<byte> sink = new();
        using Utf8JsonWriter writer = new(sink);
        WriteStreamingEnvelope(writer, OpcodeCount);
        return sink.WrittenCount;
    }

    [Benchmark(Description = "PeakHeap: 16 concurrent buffered traces, output drained (pipe-like)")]
    public long PeakHeap_Buffered()
    {
        const int Concurrency = 16;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long baseline = GC.GetTotalMemory(true);

        List<GethLikeTxTrace> live = new(Concurrency);
        for (int n = 0; n < Concurrency; n++)
        {
            List<GethTxTraceEntry> entries = new(OpcodeCount);
            for (int i = 0; i < OpcodeCount; i++) entries.Add(BuildEntry(i));
            GethLikeTxTrace trace = new() { Entries = entries, Gas = 21000, ReturnValue = [] };

            DiscardingBufferWriter sink = new();
            using Utf8JsonWriter writer = new(sink);
            JsonSerializer.Serialize(writer, trace, EthereumJsonSerializer.JsonOptions);

            live.Add(trace);
        }

        long peak = GC.GetTotalMemory(false);
        GC.KeepAlive(live);
        return peak - baseline;
    }

    [Benchmark(Description = "PeakHeap: 16 concurrent streaming traces, output drained (pipe-like)")]
    public long PeakHeap_Streaming()
    {
        const int Concurrency = 16;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long baseline = GC.GetTotalMemory(true);

        List<DiscardingBufferWriter> live = new(Concurrency);
        for (int n = 0; n < Concurrency; n++)
        {
            DiscardingBufferWriter sink = new();
            using Utf8JsonWriter writer = new(sink);
            WriteStreamingEnvelope(writer, OpcodeCount);
            live.Add(sink);
        }

        long peak = GC.GetTotalMemory(false);
        GC.KeepAlive(live);
        return peak - baseline;
    }

    [Benchmark(Description = "PeakHeap (real tracer): 16 concurrent buffered GethLikeTxMemoryTracer instances driven through N opcodes")]
    public long PeakHeap_BufferedWithTracer()
    {
        const int Concurrency = 16;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long baseline = GC.GetTotalMemory(true);

        List<GethLikeTxMemoryTracer> live = new(Concurrency);
        for (int n = 0; n < Concurrency; n++)
        {
            GethLikeTxMemoryTracer tracer = new(_tx, GethTraceOptions.Default);
            DriveGethOpcodes(tracer, OpcodeCount);
            DiscardingBufferWriter sink = new();
            using Utf8JsonWriter writer = new(sink);
            JsonSerializer.Serialize(writer, tracer.BuildResult(), EthereumJsonSerializer.JsonOptions);
            live.Add(tracer);
        }

        long peak = GC.GetTotalMemory(false);
        GC.KeepAlive(live);
        return peak - baseline;
    }

    [Benchmark(Description = "PeakHeap (real tracer): 16 concurrent GethLikeTxDirectStreamingTracer instances driven through N opcodes")]
    public long PeakHeap_StreamingWithTracer()
    {
        const int Concurrency = 16;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long baseline = GC.GetTotalMemory(true);

        List<GethLikeTxDirectStreamingTracer> live = new(Concurrency);
        List<DiscardingBufferWriter> sinks = new(Concurrency);
        List<Utf8JsonWriter> writers = new(Concurrency);
        for (int n = 0; n < Concurrency; n++)
        {
            DiscardingBufferWriter sink = new();
            Utf8JsonWriter writer = new(sink);
            writer.WriteStartObject();
            writer.WritePropertyName("structLogs"u8);
            writer.WriteStartArray();
            GethLikeTxDirectStreamingTracer tracer = new(_tx, GethTraceOptions.Default, writer, pipeWriter: null, CancellationToken.None);
            DriveGethOpcodes(tracer, OpcodeCount);
            GethLikeTxTrace trace = tracer.BuildResult();
            writer.WriteEndArray();
            writer.WriteNumber("gas"u8, trace.Gas);
            writer.WriteBoolean("failed"u8, false);
            writer.WritePropertyName("returnValue"u8);
            JsonSerializer.Serialize(writer, Array.Empty<byte>(), EthereumJsonSerializer.JsonOptions);
            writer.WriteEndObject();
            live.Add(tracer);
            sinks.Add(sink);
            writers.Add(writer);
        }

        long peak = GC.GetTotalMemory(false);
        GC.KeepAlive(live);
        GC.KeepAlive(sinks);
        foreach (Utf8JsonWriter w in writers) w.Dispose();
        return peak - baseline;
    }

    private void DriveGethOpcodes(GethLikeTxTracer tracer, int opcodeCount)
    {
        for (int op = 0; op < opcodeCount; op++)
        {
            ulong opU = (ulong)op;
            tracer.StartOperation(op, Instruction.SSTORE, 1_000_000 - opU, _env);
            tracer.ReportOperationRemainingGas(900_000 - opU);
        }
        tracer.MarkAsSuccess(Address.Zero, default, [], []);
    }

    private static void WriteStreamingEnvelope(Utf8JsonWriter writer, int opcodeCount)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("structLogs"u8);
        writer.WriteStartArray();

        for (int i = 0; i < opcodeCount; i++)
        {
            WriteSyntheticOpcode(writer, i);
        }

        writer.WriteEndArray();
        writer.WriteNumber("gas"u8, 21000);
        writer.WriteBoolean("failed"u8, false);
        writer.WritePropertyName("returnValue"u8);
        JsonSerializer.Serialize(writer, Array.Empty<byte>(), EthereumJsonSerializer.JsonOptions);
        writer.WriteEndObject();
    }

    private static void WriteSyntheticOpcode(Utf8JsonWriter writer, int pc)
    {
        writer.WriteStartObject();
        writer.WriteNumber("pc"u8, pc);
        writer.WriteString("op"u8, "ADD");
        writer.WriteNumber("gas"u8, 1_000_000 - pc);
        writer.WriteNumber("gasCost"u8, 3);
        writer.WriteNumber("depth"u8, 1);
        writer.WriteNull("error"u8);
        writer.WriteStartArray("stack"u8);
        writer.WriteStringValue("0x1");
        writer.WriteStringValue("0x2");
        writer.WriteEndArray();
        writer.WriteStartArray("memory"u8);
        writer.WriteStringValue("0x0000000000000000000000000000000000000000000000000000000000000000");
        writer.WriteEndArray();
        writer.WriteStartObject("storage"u8);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static GethTxMemoryTraceEntry BuildEntry(int pc) => new()
    {
        ProgramCounter = pc,
        Opcode = "ADD",
        Depth = 1,
        Gas = (ulong)(1_000_000 - pc),
        GasCost = 3,
        Memory = new byte[EvmPooledMemory.WordSize],
        Stack = BuildStackBytes(),
        Error = null,
        Storage = new Dictionary<UInt256, UInt256>(),
    };

    private static byte[] BuildStackBytes()
    {
        byte[] bytes = new byte[EvmStack.WordSize * 2];
        bytes[EvmStack.WordSize - 1] = 1;
        bytes[^1] = 2;
        return bytes;
    }

    [Params(50, 500, 5000)]
    public int TxCountPerBlock { get; set; }

    [Benchmark(Description = "Tracer lifecycle: fresh GethLikeTxDirectStreamingTracer per tx (pre-#11730 behaviour)")]
    public int Tracer_PerTxNew()
    {
        DiscardingBufferWriter sink = new();
        using Utf8JsonWriter writer = new(sink);
        for (int t = 0; t < TxCountPerBlock; t++)
        {
            GethLikeTxDirectStreamingTracer tracer = new(null, GethTraceOptions.Default, writer, null, default);
            GethLikeTxTrace _ = tracer.BuildResult();
            tracer.ReleaseResources();
        }
        return sink.WrittenCount;
    }

    [Benchmark(Description = "Tracer lifecycle: one tracer reused across N txs via ResetForNextTx (#11730 behaviour)")]
    public int Tracer_ReusedAcrossTxs()
    {
        DiscardingBufferWriter sink = new();
        using Utf8JsonWriter writer = new(sink);
        GethLikeTxDirectStreamingTracer tracer = new(null, GethTraceOptions.Default, writer, null, default);
        for (int t = 0; t < TxCountPerBlock; t++)
        {
            if (t > 0) tracer.ResetForNextTx(null);
            GethLikeTxTrace _ = tracer.BuildResult();
        }
        tracer.ReleaseResources();
        return sink.WrittenCount;
    }

    // Mirrors PipeWriter's drain-immediately behaviour: bytes written via Advance are dropped.
    // Lets us measure entry/serializer overhead without inflating peak heap with accumulated output.
    private sealed class DiscardingBufferWriter : IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[4096];
        public int WrittenCount { get; private set; }

        public void Advance(int count) => WrittenCount += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint > _buffer.Length) _buffer = new byte[Math.Max(sizeHint, _buffer.Length * 2)];
            return _buffer;
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }
}
