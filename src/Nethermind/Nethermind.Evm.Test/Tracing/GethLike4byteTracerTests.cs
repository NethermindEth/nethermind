// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native.FourByte;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

[TestFixture]
public class GethLike4byteTracerTests : VirtualMachineTestsBase
{
    [TestCaseSource(nameof(FourByteTracerTests))]
    public Dictionary<string, int>? four_byte_tracer_executes_correctly(byte[] code, byte[]? input) =>
        (Dictionary<string, int>)ExecuteAndTrace(code, input).CustomTracerResult?.Value;

    private GethLikeTxTrace ExecuteAndTrace(
        byte[] code,
        byte[]? input = default,
        UInt256 value = default)
    {
        Native4ByteTracer tracer = new Native4ByteTracer(GethTraceOptions.Default);
        (Block block, Transaction transaction) = input is null ? PrepareTx(Activation, 100000, code) : PrepareTx(Activation, 100000, code, input, value);
        _processor.Execute(transaction, block.Header, tracer);
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
                    { "62b15678-1", 2 },
                    { "00000000-2", 1 }
                }
            };

            byte[] delegateCallEvmCode = Prepare.EvmCode
                .DelegateCall(TestItem.AddressC, 50000)
                .STOP()
                .Done;
            var singleCall4ByteIds = new Dictionary<string, int>
                {
                    { "62b15678-1", 1 }
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
