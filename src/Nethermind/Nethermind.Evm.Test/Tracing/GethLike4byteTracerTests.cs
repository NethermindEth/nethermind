// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Precompiles;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.FourByte;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

[TestFixture]
public class GethLike4byteTracerTests : VirtualMachineTestsBase
{
    [Test]
    public void ReportAction_WithoutOpcodeCallbacks_CountsOnlyCalls(
        [Values(ExecutionType.TRANSACTION, ExecutionType.CALL, ExecutionType.CALLCODE, ExecutionType.DELEGATECALL, ExecutionType.STATICCALL,
            ExecutionType.CREATE, ExecutionType.CREATE2)] ExecutionType callType,
        [Values] bool precompile, [Values] bool nested)
    {
        using Native4ByteTracer tracer = new(Build.A.Transaction.TestObject,
            GethTraceOptions.Default with { EnableMemory = true, EnableReturnData = true });
        if (nested)
            tracer.ReportAction(100, UInt256.Zero, TestItem.AddressA, TestItem.AddressB, default, ExecutionType.TRANSACTION);
        tracer.ReportAction(50, UInt256.Zero, TestItem.AddressB, TestItem.AddressC,
            new byte[] { 1, 2, 3, 4 }, callType, precompile);
        Dictionary<string, int> result = ToDictionary(tracer.BuildResult());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Count, Is.EqualTo((callType.IsAnyCall() || callType == ExecutionType.TRANSACTION) && !precompile ? 1 : 0));
            Assert.That(tracer.IsTracingInstructions, Is.False);
            Assert.That(tracer.IsTracingStack, Is.False);
            Assert.That(tracer.IsTracingOpLevelStorage, Is.False);
            Assert.That(tracer.IsTracingReturnData, Is.False);
        }
    }

    [Test]
    public void ReportAction_CountsRepeatedIdsAndFormatsSelectorAndSize()
    {
        using Native4ByteTracer tracer = new(Build.A.Transaction.TestObject, GethTraceOptions.Default);
        byte[] longInput = new byte[4 + 1234];
        longInput[0] = 0xde;
        longInput[1] = 0xad;
        longInput[2] = 0xbe;
        longInput[3] = 0xef;

        tracer.ReportAction(100, UInt256.Zero, TestItem.AddressA, TestItem.AddressB, longInput, ExecutionType.TRANSACTION);
        tracer.ReportAction(50, UInt256.Zero, TestItem.AddressB, TestItem.AddressC, new byte[] { 0xde, 0xad, 0xbe, 0xef }, ExecutionType.CALL);
        tracer.ReportAction(25, UInt256.Zero, TestItem.AddressC, TestItem.AddressB, longInput, ExecutionType.CALL);
        tracer.ReportAction(10, UInt256.Zero, TestItem.AddressB, TestItem.AddressC, new byte[] { 0, 0, 0, 1, 0 }, ExecutionType.CALL);

        Assert.That(Serialize(tracer.BuildResult()), Is.EqualTo("""{"0xdeadbeef-1234":2,"0xdeadbeef-0":1,"0x00000001-1":1}"""));
    }

    [Test]
    public void FourByteId_formats_the_longest_id_within_the_key_buffer()
    {
        Span<byte> key = stackalloc byte[FourByteId.MaxUtf8Length];
        int length = new FourByteId(uint.MaxValue, int.MaxValue).FormatUtf8(key);

        Assert.That(Encoding.UTF8.GetString(key[..length]), Is.EqualTo("0xffffffff-2147483647"));
    }

    [TestCaseSource(nameof(FourByteTracerTests))]
    public Dictionary<string, int>? four_byte_tracer_executes_correctly(byte[] code, byte[]? input) =>
        ToDictionary(ExecuteAndTrace(code, input));

    private static string Serialize(GethLikeTxTrace trace) => new EthereumJsonSerializer().Serialize(trace.CustomTracerResult);

    private static Dictionary<string, int> ToDictionary(GethLikeTxTrace trace) =>
        JsonSerializer.Deserialize<Dictionary<string, int>>(Serialize(trace))!;

    private GethLikeTxTrace ExecuteAndTrace(
        byte[] code,
        byte[]? input = default,
        UInt256 value = default)
    {
        (Block block, Transaction transaction) = input is null ? PrepareTx(Activation, 100000, code) : PrepareTx(Activation, 100000, code, input, value);
        Native4ByteTracer tracer = new(transaction, GethTraceOptions.Default);
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, Spec), tracer);
        return tracer.BuildResult();
    }

    private static IEnumerable FourByteTracerTests
    {
        get
        {
            byte[] sampleInput = Prepare.EvmCode
                .PushData(SampleHexData2)
                .STOP()
                .Done;
            byte[] callEvmCode = Prepare.EvmCode
                .CallWithInput(TestItem.AddressA, 50000, sampleInput)
                .CallWithInput(TestItem.AddressA, 50000, sampleInput)
                .CallWithInput(TestItem.AddressA, 50000, new byte[6])
                .STOP()
                .Done;
            yield return new TestCaseData(callEvmCode, null)
            {
                TestName = "Tracing CALL execution",
                ExpectedResult = new Dictionary<string, int>
                {
                    { "0x62b15678-1", 2 },
                    { "0x00000000-2", 1 }
                }
            };

            byte[] delegateCallEvmCode = Prepare.EvmCode
                .DelegateCall(TestItem.AddressC, 50000)
                .STOP()
                .Done;
            Dictionary<string, int> singleCall4ByteIds = new()
            {
                    { "0x62b15678-1", 1 }
                };
            yield return new TestCaseData(delegateCallEvmCode, sampleInput)
            {
                TestName = "Tracing DELEGATECALL execution",
                ExpectedResult = singleCall4ByteIds
            };

            byte[] staticCallEvmCode = Prepare.EvmCode
                .StaticCall(TestItem.AddressC, 50000)
                .STOP()
                .Done;
            yield return new TestCaseData(staticCallEvmCode, sampleInput)
            {
                TestName = "Tracing STATICCALL execution",
                ExpectedResult = singleCall4ByteIds
            };

            byte[] callCodeEvmCode = Prepare.EvmCode
                .CallCode(TestItem.AddressC, 50000)
                .STOP()
                .Done;
            yield return new TestCaseData(callCodeEvmCode, sampleInput)
            {
                TestName = "Tracing CALLCODE execution",
                ExpectedResult = singleCall4ByteIds
            };

            byte[] callEvmCodeLessThan3Bytes = Prepare.EvmCode
                .CallWithInput(TestItem.AddressA, 50000, Prepare.EvmCode.STOP().Done)
                .CallWithInput(TestItem.AddressA, 50000, new byte[3])
                .STOP()
                .Done;
            yield return new TestCaseData(callEvmCodeLessThan3Bytes, null)
            {
                TestName = "Tracing CALL execution with input less than 4 bytes",
                ExpectedResult = new Dictionary<string, int>()
            };

            byte[] callEvmCodePrecompile = Prepare.EvmCode
                .CallWithInput(IdentityPrecompile.Address, 50000, sampleInput)
                .STOP()
                .Done;
            yield return new TestCaseData(callEvmCodePrecompile, null)
            {
                TestName = "Tracing CALL execution with precompile",
                ExpectedResult = new Dictionary<string, int>()
            };
        }
    }
}
