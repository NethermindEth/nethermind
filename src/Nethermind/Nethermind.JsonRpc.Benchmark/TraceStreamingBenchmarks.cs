// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Benchmark;

[MemoryDiagnoser]
public class TraceStreamingBenchmarks
{
    [Params(1_000, 10_000, 100_000)]
    public int OpcodeCount { get; set; }

    private Block _block = null!;
    private Transaction _tx = null!;
    private ExecutionEnvironment _env;
    private byte[] _value32 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tx = Build.A.Transaction.WithTo(TestItem.AddressA).TestObject;
        _block = Build.A.Block.WithTransactions(_tx).TestObject;
        _env = ExecutionEnvironment.Rent(CodeInfo.Empty, Address.Zero, Address.Zero, null, 1, default, default);
        _value32 = new byte[32];
    }

    [Benchmark(Baseline = true, Description = "Throughput: buffered — accumulate N opcode entries, serialize the full vmTrace envelope")]
    public int Throughput_Buffered()
    {
        ParityVmOperationTrace[] ops = new ParityVmOperationTrace[OpcodeCount];
        for (int i = 0; i < OpcodeCount; i++) ops[i] = BuildOp(i);

        ParityVmTrace vmTrace = new() { Code = [], Operations = ops };
        ParityLikeTxTrace trace = new() { VmTrace = vmTrace, Output = [] };

        ArrayBufferWriter<byte> sink = new();
        using Utf8JsonWriter writer = new(sink);
        JsonSerializer.Serialize(writer, new ParityTxTraceFromReplay(trace, true), EthereumJsonSerializer.JsonOptions);
        return sink.WrittenCount;
    }

    [Benchmark(Description = "Throughput: streaming — emit each opcode inline via direct Utf8JsonWriter writes")]
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

        List<ParityLikeTxTrace> live = new(Concurrency);
        for (int n = 0; n < Concurrency; n++)
        {
            ParityVmOperationTrace[] ops = new ParityVmOperationTrace[OpcodeCount];
            for (int i = 0; i < OpcodeCount; i++) ops[i] = BuildOp(i);
            ParityVmTrace vmTrace = new() { Code = [], Operations = ops };
            ParityLikeTxTrace trace = new() { VmTrace = vmTrace, Output = [] };

            DiscardingBufferWriter sink = new();
            using Utf8JsonWriter writer = new(sink);
            JsonSerializer.Serialize(writer, new ParityTxTraceFromReplay(trace, true), EthereumJsonSerializer.JsonOptions);

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

    [Benchmark(Description = "PeakHeap (real tracer): 16 concurrent buffered ParityLikeTxTracer instances driven through N opcodes")]
    public long PeakHeap_BufferedWithTracer()
    {
        const int Concurrency = 16;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long baseline = GC.GetTotalMemory(true);

        List<ParityLikeTxTracer> live = new(Concurrency);
        for (int n = 0; n < Concurrency; n++)
        {
            ParityLikeTxTracer tracer = new(_block, _tx, ParityTraceTypes.Trace | ParityTraceTypes.VmTrace);
            DriveOpcodes(tracer, OpcodeCount);
            DiscardingBufferWriter sink = new();
            using Utf8JsonWriter writer = new(sink);
            JsonSerializer.Serialize(writer, new ParityTxTraceFromReplay(tracer.BuildResult(), true), EthereumJsonSerializer.JsonOptions);
            live.Add(tracer);
        }

        long peak = GC.GetTotalMemory(false);
        GC.KeepAlive(live);
        return peak - baseline;
    }

    [Benchmark(Description = "PeakHeap (real tracer): 16 concurrent StreamingParityLikeTxTracer instances driven through N opcodes")]
    public long PeakHeap_StreamingWithTracer()
    {
        const int Concurrency = 16;
        JsonSerializerOptions opts = EthereumJsonSerializer.JsonOptions;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long baseline = GC.GetTotalMemory(true);

        List<StreamingParityLikeTxTracer> live = new(Concurrency);
        List<DiscardingBufferWriter> sinks = new(Concurrency);
        List<Utf8JsonWriter> writers = new(Concurrency);
        for (int n = 0; n < Concurrency; n++)
        {
            DiscardingBufferWriter sink = new();
            Utf8JsonWriter writer = new(sink);
            writer.WriteStartObject();
            writer.WritePropertyName("vmTrace"u8);
            StreamingParityLikeTxTracer tracer = new(
                _block, _tx, ParityTraceTypes.Trace | ParityTraceTypes.VmTrace,
                writer, pipeWriter: null, CancellationToken.None,
                fillVmTraceSlot: true);
            DriveOpcodes(tracer, OpcodeCount);
            ParityLikeTxTrace trace = tracer.BuildResult();
            ParityReplayEnvelopeWriter.WriteTail(writer, trace, includeTxHash: true, opts);
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

    private void DriveOpcodes(ParityLikeTxTracer tracer, int opcodeCount)
    {
        ReadOnlySpan<byte> value = _value32;
        tracer.ReportAction(1_000_000, UInt256.Zero, Address.Zero, Address.Zero, default, ExecutionType.CALL);
        for (int op = 0; op < opcodeCount; op++)
        {
            tracer.StartOperation(op, Instruction.SSTORE, (ulong)(1_000_000 - op), _env);
            tracer.ReportStackPush(value);
            tracer.ReportStorageChange(value, value);
            tracer.ReportOperationRemainingGas((ulong)(900_000 - op));
        }
        tracer.ReportActionEnd(0, default);
        tracer.MarkAsSuccess(Address.Zero, default, [], []);
    }

    private static void WriteStreamingEnvelope(Utf8JsonWriter writer, int opcodeCount)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("vmTrace"u8);
        writer.WriteStartObject();
        writer.WritePropertyName("code"u8);
        ByteArrayConverter.Convert(writer, ReadOnlySpan<byte>.Empty, skipLeadingZeros: false);
        writer.WritePropertyName("ops"u8);
        writer.WriteStartArray();
        for (int i = 0; i < opcodeCount; i++) WriteSyntheticOpcode(writer, i);
        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.WritePropertyName("output"u8);
        JsonSerializer.Serialize(writer, Array.Empty<byte>(), EthereumJsonSerializer.JsonOptions);
        writer.WritePropertyName("stateDiff"u8);
        writer.WriteNullValue();
        writer.WritePropertyName("trace"u8);
        writer.WriteStartArray();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteSyntheticOpcode(Utf8JsonWriter writer, int pc)
    {
        writer.WriteStartObject();
        writer.WriteNumber("cost"u8, 3L);
        writer.WritePropertyName("ex"u8);
        writer.WriteStartObject();
        writer.WriteNull("mem"u8);
        writer.WriteStartArray("push"u8);
        writer.WriteEndArray();
        writer.WriteNull("store"u8);
        writer.WriteNumber("used"u8, 1_000_000L - pc);
        writer.WriteEndObject();
        writer.WriteNumber("pc"u8, pc);
        writer.WriteNull("sub"u8);
        writer.WriteEndObject();
    }

    private static ParityVmOperationTrace BuildOp(int pc) => new()
    {
        Pc = pc,
        Cost = 3,
        Used = (ulong)(1_000_000 - pc),
        Push = [],
    };

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
