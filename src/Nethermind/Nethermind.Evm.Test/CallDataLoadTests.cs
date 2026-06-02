// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[Parallelizable(ParallelScope.Self)]
public class CallDataLoadTests : VirtualMachineTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.CancunBlockTimestamp;

    // Forces the OffFlag specialization of InstructionCallDataLoad; the default tracer
    // has IsTracingInstructions = true which picks the OnFlag path.
    protected override TestAllTracerWithOutput CreateTracer() => new NoInstructionTracer();

    private sealed class NoInstructionTracer : TestAllTracerWithOutput
    {
        public override bool IsTracingInstructions => false;
    }

    private static readonly byte[] ThirtyTwoSequential = BuildSequential(32);
    private static readonly byte[] FiveBytes = [0x11, 0x22, 0x33, 0x44, 0x55];
    private static readonly byte[] TwoBytes = [0xAA, 0xBB];

    private static byte[] BuildSequential(int n)
    {
        byte[] b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(i + 1);
        return b;
    }

    private static byte[] RightPadded(ReadOnlySpan<byte> head, int total = 32)
    {
        byte[] result = new byte[total];
        head.CopyTo(result);
        return result;
    }

    private static byte[] OffsetAsBigEndian(UInt256 offset)
    {
        byte[] b = new byte[32];
        offset.ToBigEndian(b);
        return b;
    }

    private void RunAndAssert(byte[] calldata, UInt256 offset, byte[] expected)
    {
        byte[] code = Prepare.EvmCode
            .PushData(OffsetAsBigEndian(offset))
            .Op(Instruction.CALLDATALOAD)
            .PushData((byte)0x00)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        (Block block, Transaction tx) = PrepareTx(Activation, 100_000, code, calldata, value: 0);
        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(Activation)), tracer);

        AssertStorage((UInt256)0, (ReadOnlySpan<byte>)expected);
    }

    public static IEnumerable<TestCaseData> CallDataLoadCases()
    {
        yield return new TestCaseData(ThirtyTwoSequential, (UInt256)0, ThirtyTwoSequential)
            .SetName("offset_zero_full_32_bytes");

        yield return new TestCaseData(TwoBytes, (UInt256)5, new byte[32])
            .SetName("offset_past_end_returns_zero");

        yield return new TestCaseData(FiveBytes, (UInt256)2, RightPadded([0x33, 0x44, 0x55]))
            .SetName("offset_inside_partial_right_pad");

        // u0=0, u1=1 ⇒ numeric value = 2^64, exercises the !IsUint64 branch.
        yield return new TestCaseData(ThirtyTwoSequential, new UInt256(0UL, 1UL, 0UL, 0UL), new byte[32])
            .SetName("offset_u1_set_returns_zero");

        yield return new TestCaseData(ThirtyTwoSequential, (UInt256)31, RightPadded([ThirtyTwoSequential[31]]))
            .SetName("offset_at_last_byte_one_byte_right_padded");

        yield return new TestCaseData(ThirtyTwoSequential, (UInt256)32, new byte[32])
            .SetName("offset_equals_length_returns_zero");

        yield return new TestCaseData(Array.Empty<byte>(), (UInt256)0, new byte[32])
            .SetName("empty_calldata_returns_zero");

        // 0x_1_0000_0009: low 32 bits = 9, true value > 4 billion. A buggy truncate-to-uint
        // implementation would read bytes[9..32]||zeros instead of the spec-correct zero word.
        yield return new TestCaseData(ThirtyTwoSequential, (UInt256)((ulong)uint.MaxValue + 10UL), new byte[32])
            .SetName("offset_above_uint32_returns_zero");
    }

    [TestCaseSource(nameof(CallDataLoadCases))]
    public void CallDataLoad_returns_expected_word(byte[] calldata, UInt256 offset, byte[] expected)
        => RunAndAssert(calldata, offset, expected);

    [Test]
    public void CallDataLoad_empty_stack_underflows()
    {
        byte[] code = Prepare.EvmCode
            .Op(Instruction.CALLDATALOAD)
            .Done;

        TestAllTracerWithOutput tracer = Execute(code);
        Assert.That(tracer.Error, Is.EqualTo(EvmExceptionType.StackUnderflow.ToString()));
    }
}
