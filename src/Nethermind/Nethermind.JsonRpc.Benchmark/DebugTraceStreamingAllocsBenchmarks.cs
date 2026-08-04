// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Benchmark;

[MemoryDiagnoser]
public class DebugTraceStreamingAllocsBenchmarks
{
    [Params(20)] public int TxsPerBlock { get; set; }
    [Params(100)] public int OpcodesPerTx { get; set; }

    private GethTraceOptions _options = null!;
    private ExecutionEnvironment[] _envByDepth = null!;
    private byte[] _valueBuffer = null!;
    private static readonly Action<GethLikeTxDirectStreamingTracer, Transaction?> _resetForNextTx =
        (Action<GethLikeTxDirectStreamingTracer, Transaction?>)Delegate.CreateDelegate(
            typeof(Action<GethLikeTxDirectStreamingTracer, Transaction?>),
            typeof(GethLikeTxDirectStreamingTracer).GetMethod("ResetForNextTx", BindingFlags.Instance | BindingFlags.NonPublic)!);
    private static readonly Action<GethLikeTxDirectStreamingTracer> _releaseResources =
        (Action<GethLikeTxDirectStreamingTracer>)Delegate.CreateDelegate(
            typeof(Action<GethLikeTxDirectStreamingTracer>),
            typeof(GethLikeTxDirectStreamingTracer).GetMethod("ReleaseResources", BindingFlags.Instance | BindingFlags.NonPublic)!);

    [GlobalSetup]
    public void Setup()
    {
        _options = new GethTraceOptions { DisableStack = true, EnableMemory = false };
        _envByDepth = new ExecutionEnvironment[5];
        for (int d = 0; d < 5; d++)
        {
            _envByDepth[d] = ExecutionEnvironment.Rent(
                CodeInfo.Empty, Address.Zero, Address.Zero, null, d, default, default);
        }
        _valueBuffer = new byte[32];
    }

    [Benchmark(Baseline = true, Description = "Per-tx tracer: original behavior, allocate+release per tx")]
    public int PerTxTracer()
    {
        ArrayBufferWriter<byte> sink = new();
        using Utf8JsonWriter writer = new(sink, new JsonWriterOptions { SkipValidation = true });
        for (int txIdx = 0; txIdx < TxsPerBlock; txIdx++)
        {
            GethLikeTxDirectStreamingTracer tracer = new(null, _options, writer, null, CancellationToken.None);
            RunTx(tracer);
            _releaseResources(tracer);
        }
        return sink.WrittenCount;
    }

    [Benchmark(Description = "Reused tracer: one instance, reset between txs, release at end")]
    public int ReusedTracer()
    {
        ArrayBufferWriter<byte> sink = new();
        using Utf8JsonWriter writer = new(sink, new JsonWriterOptions { SkipValidation = true });
        GethLikeTxDirectStreamingTracer tracer = new(null, _options, writer, null, CancellationToken.None);
        try
        {
            for (int txIdx = 0; txIdx < TxsPerBlock; txIdx++)
            {
                if (txIdx > 0) _resetForNextTx(tracer, null);
                RunTx(tracer);
            }
        }
        finally
        {
            _releaseResources(tracer);
        }
        return sink.WrittenCount;
    }

    private void RunTx(GethLikeTxDirectStreamingTracer tracer)
    {
        ReadOnlySpan<byte> valueSpan = _valueBuffer;
        for (int op = 0; op < OpcodesPerTx; op++)
        {
            ulong opU = (ulong)op;
            int depth = (op % 4) + 1;
            tracer.StartOperation(op, Instruction.SSTORE, 1_000_000 - opU, _envByDepth[depth - 1]);
            tracer.SetOperationStorage(Address.Zero, new UInt256(opU), valueSpan, valueSpan);
            tracer.ReportOperationRemainingGas(900_000 - opU);
        }
        tracer.BuildResult();
    }
}
