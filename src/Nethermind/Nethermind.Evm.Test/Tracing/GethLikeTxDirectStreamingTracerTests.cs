// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class GethLikeTxDirectStreamingTracerTests : GethLikeTracerTestsBase
{
    [TestCase(Instruction.PREVRANDAO, "DIFFICULTY")]
    [TestCase((Instruction)0xd0, "DATALOAD")]
    [TestCase((Instruction)0x0f, "opcode 0xf not defined")]
    public void Streams_geth_opcode_name(Instruction opcode, string expectedName)
    {
        List<StructLog> logs = StreamLogs(GethTraceOptions.Default, tracer =>
        {
            using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
                null!, Address.Zero, Address.Zero, null, callDepth: 0, value: UInt256.Zero, inputData: default);

            tracer.StartOperation(0, opcode, 100, in environment);
            tracer.ReportOperationRemainingGas(100);
        });

        Assert.That(logs.Single().Op, Is.EqualTo(expectedName));
    }

    [TestCase(false, TestName = "Refund accumulates and persists after a clearing SSTORE")]
    [TestCase(true, TestName = "Refund is rolled back when the clearing frame reverts")]
    public void Streams_journaled_refund_counter(bool clearingFrameReverts)
    {
        ulong sClearRefund = Spec.GasCosts.SClearRefund;

        List<StructLog> logs = ExecuteAndStream(clearingFrameReverts);

        using (Assert.EnterMultipleScope())
        {
            if (clearingFrameReverts)
            {
                Assert.That(RefundAt(logs, "REVERT"), Is.EqualTo(sClearRefund));
                Assert.That(logs.Last(l => l.Op == "STOP" && l.Depth == 1).Refund, Is.Null);
            }
            else
            {
                Assert.That(RefundAt(logs, "SSTORE"), Is.EqualTo(sClearRefund));
                Assert.That(RefundAt(logs, "STOP"), Is.EqualTo(sClearRefund));
            }
        }
    }

    private static long? RefundAt(IEnumerable<StructLog> logs, string opcode) => logs.Single(l => l.Op == opcode).Refund;

    [Test]
    public void Streams_parent_refund_survives_child_revert()
    {
        List<StructLog> logs = StreamLogs(RefundThenChildRevertCode(), GethTraceOptions.Default);

        Assert.That(
            logs.Last(l => l is { Op: "STOP", Depth: 1 }).Refund, Is.EqualTo(Spec.GasCosts.SClearRefund),
            "parent refund must persist after the child frame reverts"
        );
    }

    [Test]
    public void Streams_legacy_self_destruct_refund_after_child_returns()
    {
        const long destroyRefund = (long)RefundOf.DestroyBeforeEip3529;
        List<StructLog> logs = StreamLogs(GethTraceOptions.Default, tracer =>
        {
            using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
                null!, Address.Zero, Address.Zero, null, callDepth: 0, value: UInt256.Zero, inputData: default);
            using ExecutionEnvironment childEnvironment = ExecutionEnvironment.Rent(
                null!, TestItem.AddressA, Address.Zero, null, callDepth: 1, value: UInt256.Zero, inputData: default);

            tracer.ReportAction(100, UInt256.Zero, Address.Zero, Address.Zero, default, ExecutionType.TRANSACTION);
            tracer.ReportAction(50, UInt256.Zero, Address.Zero, TestItem.AddressA, default, ExecutionType.CALL);
            tracer.StartOperation(0, Instruction.SELFDESTRUCT, 50, in childEnvironment);
            tracer.ReportSelfDestruct(TestItem.AddressA, UInt256.Zero, Address.Zero);
            tracer.ReportSelfDestruct(TestItem.AddressA, UInt256.Zero, Address.Zero);
            tracer.ReportOperationRemainingGas(25);
            tracer.ReportActionEnd(25, default);
            tracer.StartOperation(0, Instruction.STOP, 50, in environment);
            tracer.ReportOperationRemainingGas(50);
            tracer.ReportActionEnd(50, default);
            tracer.ReportRefund(destroyRefund);
        }, destroyRefund: destroyRefund);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RefundAt(logs, "SELFDESTRUCT"), Is.EqualTo(destroyRefund));
            Assert.That(RefundAt(logs, "STOP"), Is.EqualTo(destroyRefund));
        }
    }

    [Test]
    public void Streams_returndata_when_enabled()
    {
        const string word = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

        List<StructLog> logs = StreamLogs(ReturnDataCallCode(word), GethTraceOptions.Default with { EnableReturnData = true });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(logs.Last(l => l.Op == "CALL").ReturnData, Is.Null, "no return data before the call executes");
            Assert.That(logs.Last(l => l.Op == "STOP" && l.Depth == 1).ReturnData, Is.EqualTo($"0x{word}"));
        }
    }

    [Test]
    public void Omits_returndata_when_not_enabled()
    {
        const string word = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

        List<StructLog> logs = StreamLogs(ReturnDataCallCode(word), GethTraceOptions.Default);

        Assert.That(logs.All(l => l.ReturnData is null), Is.True);
    }

    private const int DeepStackDepth = 512;
    private const int MaxEntryOvershootBytes = 64 * 1024;

    [TestCase(false, TestName = "StreamedTrace_WithDeepStack_FlushesBeforeThresholdIsExceeded")]
    [TestCase(true, TestName = "StreamedTrace_WithDeepStackAndGrowingMemory_FlushesBeforeThresholdIsExceeded")]
    public void StreamedTrace_WithLargeEntries_FlushesBeforeThresholdIsExceeded(bool enableMemory)
    {
        // Scenario:
        // 1. Fill the stack 512 deep, then loop writing one byte 256 bytes further into memory until out of gas.
        // 2. Every struct log carries the whole stack (and memory), so each entry is tens of KB.
        // 3. The default block tracer streams into a writer that records the bytes written between flushes.
        const long threshold = GethLikeTxDirectStreamingTracer.DefaultFlushThresholdBytes;
        FlushRecordingPipeWriter pipe = new(keepOutput: false);
        (Block block, Transaction transaction) = PrepareTx(Activation, 30_000, DeepStackLoopCode.Build(DeepStackDepth, memoryStride: 256));

        using (Utf8JsonWriter writer = new(pipe))
        using (GethLikeBlockStreamingMemoryTracer blockTracer = new(GethTraceOptions.Default with { EnableMemory = enableMemory }, writer, pipe, CancellationToken.None))
        {
            writer.WriteStartArray();
            ITxTracer tracer = ((IBlockTracer)blockTracer).StartNewTxTrace(transaction);
            _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
            blockTracer.EndTxTrace();
            blockTracer.EndBlockTrace();
            writer.WriteEndArray();
        }

        Assert.That(pipe.TotalBytes, Is.GreaterThan(8 * threshold), "precondition: the trace must span many flush windows");
        Assert.That(pipe.MaxUnflushedBytes, Is.LessThanOrEqualTo(threshold + MaxEntryOvershootBytes),
            "unflushed output must stay near the flush threshold however large each entry is, so a slow reader applies backpressure");
    }

    [Test]
    public void StreamedTrace_WithEntryLargerThanThreshold_FlushesWithinTheEntry()
    {
        // Scenario:
        // 1. MSTORE8 at offset 0xffff expands memory to 64 KB.
        // 2. Every following struct log dumps that memory, about 140 KB of JSON per entry.
        // 3. With a 4 KB threshold the tracer has to flush partway through each memory dump.
        const int threshold = 4096;
        byte[] code = Prepare.EvmCode
            .PushData(1)
            .PushData(0xffff)
            .Op(Instruction.MSTORE8)
            .PushData(0).Op(Instruction.POP)
            .PushData(0).Op(Instruction.POP)
            .PushData(0).Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        FlushRecordingPipeWriter pipe = new(keepOutput: false);
        StreamTrace(code, GethTraceOptions.Default with { EnableMemory = true }, pipe, threshold);

        Assert.That(pipe.TotalBytes, Is.GreaterThan(4 * 64 * 1024), "precondition: several entries each carry the 64 KB memory");
        Assert.That(pipe.MaxUnflushedBytes, Is.LessThanOrEqualTo(threshold + 1024),
            "a single entry larger than the threshold must not be held whole before the flush");
    }

    [TestCase(1, TestName = "StreamedTrace_FlushedAtEveryCheck_IsByteIdenticalToUnflushedTrace")]
    [TestCase(256, TestName = "StreamedTrace_FlushedEvery256Bytes_IsByteIdenticalToUnflushedTrace")]
    public void StreamedTrace_WhenFlushedWithinEntries_IsByteIdenticalToUnflushedTrace(int threshold)
    {
        const string word = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
        GethTraceOptions options = GethTraceOptions.Default with { EnableMemory = true, EnableReturnData = true };

        (ArrayBufferWriter<byte> unflushed, FlushRecordingPipeWriter flushed) = TraceUnflushedAndFlushed(StorageMemoryAndReturnDataCode(word), options, threshold);

        Assert.That(flushed.FlushCount, Is.GreaterThan(10), "precondition: the trace must be flushed many times, including within entries");
        Assert.That(flushed.WrittenSpan.SequenceEqual(unflushed.WrittenSpan), Is.True, "flushing must not change a single byte of the output");
    }

    [Test]
    public void StreamedTrace_WithLargeReturnData_FlushesWithinTheReturnDataValue()
    {
        // Scenario:
        // 1. The callee returns 1 MiB of zeroed memory to the caller.
        // 2. Each caller struct log after the call carries that return data as about 2 MiB of hex.
        // 3. With a 4 KB threshold the hex must be written and flushed in chunks, byte for byte as one string.
        const int threshold = 4096;
        const int returnDataSize = 1024 * 1024;
        byte[] calleeCode = Prepare.EvmCode
            .Return(returnDataSize, 0)
            .Done;
        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, calleeCode, Spec);
        TestState.Commit(Spec);
        byte[] code = Prepare.EvmCode
            .Call(TestItem.AddressC, 3_000_000)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        (ArrayBufferWriter<byte> unflushed, FlushRecordingPipeWriter flushed) =
            TraceUnflushedAndFlushed(code, GethTraceOptions.Default with { EnableReturnData = true }, threshold, gasLimit: 4_000_000);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flushed.TotalBytes, Is.GreaterThan(2L * 2 * returnDataSize), "precondition: two entries each carry the 1 MiB return data");
            Assert.That(flushed.MaxUnflushedBytes, Is.LessThanOrEqualTo(threshold + 4096),
                "a large return value must be flushed while its hex is written, not held as one token");
            Assert.That(flushed.WrittenSpan.SequenceEqual(unflushed.WrittenSpan), Is.True, "chunking must not change a single byte of the output");
        }
    }

    private (ArrayBufferWriter<byte> Unflushed, FlushRecordingPipeWriter Flushed) TraceUnflushedAndFlushed(
        byte[] code, GethTraceOptions options, int threshold, ulong gasLimit = 100_000)
    {
        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, code);

        ArrayBufferWriter<byte> unflushed = new();
        FlushRecordingPipeWriter flushed = new(keepOutput: true);
        using Utf8JsonWriter unflushedWriter = new(unflushed);
        using Utf8JsonWriter flushedWriter = new(flushed);
        GethLikeTxDirectStreamingTracer unflushedTracer = new(transaction, options, unflushedWriter, pipeWriter: null, CancellationToken.None, threshold);
        GethLikeTxDirectStreamingTracer flushedTracer = new(transaction, options, flushedWriter, flushed, CancellationToken.None, threshold);
        unflushedWriter.WriteStartArray();
        flushedWriter.WriteStartArray();

        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), new CompositeTxTracer(unflushedTracer, flushedTracer));
        Complete(unflushedTracer, unflushedWriter);
        Complete(flushedTracer, flushedWriter);
        return (unflushed, flushed);
    }

    private void StreamTrace(byte[] code, GethTraceOptions options, PipeWriter pipe, int flushThresholdBytes)
    {
        (Block block, Transaction transaction) = PrepareTx(Activation, 100_000, code);

        using Utf8JsonWriter writer = new(pipe);
        GethLikeTxDirectStreamingTracer tracer = new(transaction, options, writer, pipe, CancellationToken.None, flushThresholdBytes);
        writer.WriteStartArray();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        Complete(tracer, writer);
    }

    private static void Complete(GethLikeTxDirectStreamingTracer tracer, Utf8JsonWriter writer)
    {
        tracer.BuildResult();
        tracer.ReleaseResources();
        writer.WriteEndArray();
        writer.Flush();
    }

    private byte[] StorageMemoryAndReturnDataCode(string word)
    {
        byte[] calleeCode = Prepare.EvmCode
            .StoreDataInMemory(0, word)
            .Return(32, 0)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, calleeCode, Spec);
        TestState.Commit(Spec);

        return Prepare.EvmCode
            .PersistData("0x1", word)
            .PersistData("0x2", word)
            .StoreDataInMemory(64, word)
            .Call(TestItem.AddressC, 50000)
            .PushData("0x1")
            .Op(Instruction.SLOAD)
            .Op(Instruction.STOP)
            .Done;
    }

    private List<StructLog> ExecuteAndStream(bool clearingFrameReverts) =>
        StreamLogs(clearingFrameReverts ? ChildClearThenRevertCode() : ClearSstoreCode(), GethTraceOptions.Default);

    private List<StructLog> StreamLogs(byte[] code, GethTraceOptions options)
    {
        (Block block, Transaction transaction) = PrepareTx(Activation, 100000, code);

        return StreamLogs(options,
            tracer => _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer),
            transaction);
    }

    private static List<StructLog> StreamLogs(
        GethTraceOptions options,
        Action<ITxTracer> execute,
        Transaction? transaction = null,
        long destroyRefund = 0)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        using (GethLikeBlockStreamingMemoryTracer blockTracer = new(options, writer, null, CancellationToken.None, destroyRefund))
        {
            writer.WriteStartArray();
            execute(((IBlockTracer)blockTracer).StartNewTxTrace(transaction));
            blockTracer.EndTxTrace();
            blockTracer.EndBlockTrace();
            writer.WriteEndArray();
        }

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        return [.. document.RootElement.EnumerateArray().Select(static e => new StructLog(
            e.GetProperty("op").GetString()!,
            e.GetProperty("depth").GetInt32(),
            e.TryGetProperty("refund", out JsonElement refund) ? refund.GetInt64() : null,
            e.TryGetProperty("returnData", out JsonElement returnData) ? returnData.GetString() : null))];
    }

    private byte[] ReturnDataCallCode(string word)
    {
        byte[] calleeCode = Prepare.EvmCode
            .StoreDataInMemory(0, word)
            .Return(32, 0)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, calleeCode, Spec);
        TestState.Commit(Spec);

        return Prepare.EvmCode
            .Call(TestItem.AddressC, 50000)
            .Op(Instruction.STOP)
            .Done;
    }

    private readonly record struct StructLog(string Op, int Depth, long? Refund, string? ReturnData);
}
