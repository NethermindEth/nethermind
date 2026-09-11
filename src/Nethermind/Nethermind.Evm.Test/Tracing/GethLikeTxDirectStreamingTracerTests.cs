// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
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
