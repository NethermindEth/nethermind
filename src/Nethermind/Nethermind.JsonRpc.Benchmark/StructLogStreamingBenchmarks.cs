// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text.Json;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Tracing;

namespace Nethermind.JsonRpc.Benchmark;

/// <summary>Per-opcode cost of <see cref="GethLikeTxDirectStreamingTracer"/> writing struct logs with stack and memory.</summary>
[MemoryDiagnoser]
public class StructLogStreamingBenchmarks
{
    private const int OpcodeCount = 1_000;

    [Params(4, 16, 64)]
    public int StackDepth { get; set; }

    [Params(0, 1024)]
    public int MemorySize { get; set; }

    private ExecutionEnvironment _env;
    private TraceStack _stack;
    private TraceMemory _memory;
    private GethTraceOptions _options = null!;
    private readonly DiscardingBufferWriter _sink = new();

    [GlobalSetup]
    public void Setup()
    {
        _env = ExecutionEnvironment.Rent(CodeInfo.Empty, Address.Zero, Address.Zero, null, 1, default, default);

        Random random = new(42);
        byte[] stackWords = new byte[StackDepth * EvmStack.WordSize];
        for (int i = 0; i < StackDepth; i++)
        {
            // Mix small values with full-width ones such as hashes and addresses.
            int significant = (i % 3) switch { 0 => 1, 1 => 20, _ => 32 };
            random.NextBytes(stackWords.AsSpan((i + 1) * EvmStack.WordSize - significant, significant));
        }
        _stack = new TraceStack(stackWords);

        byte[] memory = new byte[MemorySize];
        for (int offset = 0; offset < memory.Length; offset += 2 * EvmPooledMemory.WordSize)
        {
            random.NextBytes(memory.AsSpan(offset, EvmPooledMemory.WordSize));
        }
        _memory = new TraceMemory((ulong)memory.Length, memory);
        _options = new GethTraceOptions { EnableMemory = MemorySize > 0 };
    }

    [Benchmark]
    public long WriteStructLogs()
    {
        using Utf8JsonWriter writer = new(_sink, new JsonWriterOptions { SkipValidation = true });
        writer.WriteStartArray();
        GethLikeTxDirectStreamingTracer tracer = new(null, _options, writer, pipeWriter: null, CancellationToken.None);
        for (int op = 0; op < OpcodeCount; op++)
        {
            tracer.StartOperation(op, Instruction.ADD, 1_000_000 - (ulong)op, _env);
            tracer.SetOperationStack(_stack);
            if (MemorySize > 0) tracer.SetOperationMemory(_memory);
            tracer.ReportOperationRemainingGas(999_997 - (ulong)op);
        }
        tracer.BuildResult();
        tracer.ReleaseResources();
        writer.WriteEndArray();
        writer.Flush();
        return writer.BytesCommitted;
    }

    private sealed class DiscardingBufferWriter : IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[4096];

        public void Advance(int count) { }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint > _buffer.Length) _buffer = new byte[Math.Max(sizeHint, _buffer.Length * 2)];
            return _buffer;
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }
}
