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
public class TraceStreamingAllocsBenchmarks
{
    [Params(200)] public int TxsPerBlock { get; set; }
    [Params(50)] public int OpcodesPerTx { get; set; }
    [Params(5)] public int CallDepthPerTx { get; set; }

    private Block _block = null!;
    private Transaction _tx = null!;
    private ExecutionEnvironment[] _envByDepth = null!;
    private byte[] _value32 = null!;
    private JsonSerializerOptions _jsonOptions = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tx = Build.A.Transaction.WithTo(TestItem.AddressA).TestObject;
        _block = Build.A.Block.WithTransactions(_tx).TestObject;
        _envByDepth = new ExecutionEnvironment[CallDepthPerTx + 1];
        for (int d = 0; d <= CallDepthPerTx; d++)
        {
            _envByDepth[d] = ExecutionEnvironment.Rent(
                CodeInfo.Empty, Address.Zero, Address.Zero, null, d, default, default);
        }
        _value32 = new byte[32];
        _jsonOptions = EthereumJsonSerializer.JsonOptions;
    }

    [Benchmark(Baseline = true, Description = "Buffered: accumulate all tx trees in memory, then serialize at end")]
    public int Buffered()
    {
        List<ParityLikeTxTrace> traces = new(TxsPerBlock);
        for (int txIdx = 0; txIdx < TxsPerBlock; txIdx++)
        {
            ParityLikeTxTracer tracer = new(_block, _tx, ParityTraceTypes.Trace | ParityTraceTypes.VmTrace);
            DriveTx(tracer);
            traces.Add(tracer.BuildResult());
        }

        ArrayBufferWriter<byte> sink = new();
        using Utf8JsonWriter writer = new(sink, new JsonWriterOptions { SkipValidation = true });
        writer.WriteStartArray();
        foreach (ParityLikeTxTrace trace in traces)
        {
            JsonSerializer.Serialize(writer, new ParityTxTraceFromReplay(trace, true), _jsonOptions);
        }
        writer.WriteEndArray();
        writer.Flush();
        return sink.WrittenCount;
    }

    [Benchmark(Description = "Streaming: pooled tracer writes JSON inline per opcode, no tx-tree accumulation")]
    public int Streaming()
    {
        ArrayBufferWriter<byte> sink = new();
        using Utf8JsonWriter writer = new(sink, new JsonWriterOptions { SkipValidation = true });
        writer.WriteStartArray();

        StreamingParityLikeTxTracer tracer = new(
            _block, _tx,
            ParityTraceTypes.Trace | ParityTraceTypes.VmTrace,
            writer, pipeWriter: null, CancellationToken.None,
            fillVmTraceSlot: true);
        try
        {
            for (int txIdx = 0; txIdx < TxsPerBlock; txIdx++)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("vmTrace"u8);
                if (txIdx > 0) tracer.ResetForNextTx(_block, _tx);
                DriveTx(tracer);
                ParityLikeTxTrace trace = tracer.BuildResult();
                ParityReplayEnvelopeWriter.WriteTail(writer, trace, includeTxHash: true, _jsonOptions);
            }
        }
        finally
        {
            tracer.ReleaseResources();
        }
        writer.WriteEndArray();
        writer.Flush();
        return sink.WrittenCount;
    }

    private void DriveTx(ParityLikeTxTracer tracer)
    {
        ReadOnlySpan<byte> value = _value32;
        for (int depth = 1; depth <= CallDepthPerTx; depth++)
        {
            tracer.ReportAction(1_000_000, UInt256.Zero, Address.Zero, Address.Zero, default, ExecutionType.CALL);
            int opsAtDepth = OpcodesPerTx / CallDepthPerTx;
            for (int op = 0; op < opsAtDepth; op++)
            {
                tracer.StartOperation(op, Instruction.SSTORE, (ulong)(1_000_000 - op), _envByDepth[depth]);
                tracer.ReportStackPush(value);
                tracer.ReportStorageChange(value, value);
                tracer.ReportOperationRemainingGas((ulong)(900_000 - op));
            }
        }
        for (int depth = 1; depth <= CallDepthPerTx; depth++)
        {
            tracer.ReportActionEnd(0, default);
        }
        tracer.MarkAsSuccess(Address.Zero, default, [], []);
    }
}
