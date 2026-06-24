// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
using Nethermind.Evm.TransactionProcessing;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class GethLikeTxDirectStreamingTracerTests : VirtualMachineTestsBase
{
    [TestCase(false, TestName = "Refund accumulates and persists after a clearing SSTORE")]
    [TestCase(true, TestName = "Refund is rolled back when the clearing frame reverts")]
    public void Streams_journaled_refund_counter(bool clearingFrameReverts)
    {
        long sClearRefund = Spec.GasCosts.SClearRefund;

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
                Assert.That(RefundAt(logs, "SSTORE"), Is.Null);
                Assert.That(RefundAt(logs, "STOP"), Is.EqualTo(sClearRefund));
            }
        }
    }

    private static long? RefundAt(IEnumerable<StructLog> logs, string opcode) => logs.Single(l => l.Op == opcode).Refund;

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
        StreamLogs(clearingFrameReverts ? RevertingCallCode() : ClearingSstoreCode(), GethTraceOptions.Default);

    private List<StructLog> StreamLogs(byte[] code, GethTraceOptions options)
    {
        (Block block, Transaction transaction) = PrepareTx(Activation, 100000, code);

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartArray();
            GethLikeTxDirectStreamingTracer tracer = new(transaction, options, writer, null, CancellationToken.None);
            _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
            tracer.BuildResult();
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

    private byte[] ClearingSstoreCode()
    {
        TestState.CreateAccount(Recipient, 1.Ether);
        TestState.Set(new StorageCell(Recipient, 0), new byte[] { 1 });
        TestState.Commit(Spec);

        return Prepare.EvmCode
            .PersistData("0x0", HexZero)
            .Op(Instruction.STOP)
            .Done;
    }

    private byte[] RevertingCallCode()
    {
        byte[] calleeCode = Prepare.EvmCode
            .PersistData("0x0", HexZero)
            .PushData(0)
            .PushData(0)
            .Op(Instruction.REVERT)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.Set(new StorageCell(TestItem.AddressC, 0), new byte[] { 1 });
        TestState.InsertCode(TestItem.AddressC, calleeCode, Spec);
        TestState.Commit(Spec);

        return Prepare.EvmCode
            .Call(TestItem.AddressC, 50000)
            .Op(Instruction.STOP)
            .Done;
    }

    private readonly record struct StructLog(string Op, int Depth, long? Refund, string? ReturnData);
}
