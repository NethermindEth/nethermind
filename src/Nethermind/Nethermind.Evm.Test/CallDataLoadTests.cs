// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[Parallelizable(ParallelScope.Self)]
[TestFixture(false)]
[TestFixture(true)]
public class CallDataLoadTests(bool tracing) : VirtualMachineTestsBase
{
    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.CancunBlockTimestamp;

    protected override TestAllTracerWithOutput CreateTracer() => new StackPushTracer(tracing);

    private sealed class StackPushTracer(bool tracing) : TestAllTracerWithOutput
    {
        public override bool IsTracingInstructions => tracing;
        public List<byte[]> Pushes { get; } = [];
        public override void ReportStackPush(in ReadOnlySpan<byte> stackItem) => Pushes.Add(stackItem.ToArray());
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
        if (tracing)
        {
            byte[] expectedPush = offset >= (UInt256)calldata.Length ? [0] : expected;
            Assert.That(((StackPushTracer)tracer).Pushes[1], Is.EqualTo(expectedPush));
        }
    }

    public static IEnumerable<TestCaseData> CallDataLoadCases()
    {
        for (int available = 1; available < 32; available++)
            yield return new TestCaseData(ThirtyTwoSequential, (UInt256)(32 - available),
                RightPadded(ThirtyTwoSequential.AsSpan(32 - available))).SetName($"partial_{available}_bytes");
        yield return new TestCaseData(ThirtyTwoSequential, UInt256.MaxValue, new byte[32]);
        yield return new TestCaseData(ThirtyTwoSequential, new UInt256(0UL, 0UL, 1UL, 0UL), new byte[32]);
        yield return new TestCaseData(ThirtyTwoSequential, new UInt256(0UL, 0UL, 0UL, 1UL), new byte[32]);
        yield return new TestCaseData(ThirtyTwoSequential, (UInt256)0, ThirtyTwoSequential)
            .SetName("offset_zero_full_32_bytes");
        yield return new TestCaseData(new byte[32], UInt256.Zero, new byte[32])
            .SetName("in_range_zero_word_preserves_full_trace_width");

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
        yield return new TestCaseData(ThirtyTwoSequential, (UInt256)(uint.MaxValue + 10UL), new byte[32])
            .SetName("offset_above_uint32_returns_zero");
    }

    [TestCaseSource(nameof(CallDataLoadCases))]
    public void CallDataLoad_returns_expected_word(byte[] calldata, UInt256 offset, byte[] expected)
        => RunAndAssert(calldata, offset, expected);

    [Test]
    public void Calldata_reads_see_each_frame_own_input_across_nested_calls()
    {
        // 1. The top frame, called with ThirtyTwoSequential, passes a different word from its memory to the child.
        // 2. The child stores its input, calls a grandchild, then reads its input again after resuming.
        // 3. The top frame reads its own input again after the child returns.
        Address child = TestItem.AddressC;
        Address grandchild = TestItem.AddressD;
        byte[] childInput = BuildSequential(32);
        Array.Reverse(childInput);

        TestState.CreateAccount(grandchild, UInt256.Zero);
        TestState.InsertCode(grandchild, Prepare.EvmCode.STOP().Done, SpecProvider.GenesisSpec);
        TestState.CreateAccount(child, UInt256.Zero);
        TestState.InsertCode(child, StoreCallDataAroundCall(grandchild, 0, Prepare.EvmCode).Done, SpecProvider.GenesisSpec);

        byte[] code = StoreCallDataAroundCall(child, childInput.Length, Prepare.EvmCode.MSTORE(0, childInput)).Done;
        (Block block, Transaction tx) = PrepareTx(Activation, 1_000_000, code, ThirtyTwoSequential, value: 0);
        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(Activation)), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success), "the transaction must succeed");
            for (int slot = 0; slot < 3; slot++)
            {
                Assert.That(ReadStorage(Recipient, slot), Is.EqualTo(ThirtyTwoSequential), $"top frame slot {slot}");
                Assert.That(ReadStorage(child, slot), Is.EqualTo(childInput), $"child frame slot {slot}");
            }
        }
    }

    /// <summary>Stores CALLDATALOAD(0) in slot 0, calls <paramref name="callee"/>, then stores CALLDATALOAD(0) and a CALLDATACOPY of it in slots 1 and 2.</summary>
    private static Prepare StoreCallDataAroundCall(Address callee, int calleeInputLength, Prepare prepare) => prepare
        .PushData(0).Op(Instruction.CALLDATALOAD).PushData(0).Op(Instruction.SSTORE)
        .CALL(200_000, callee, 0, 0, (UInt256)calleeInputLength, 0, 0).Op(Instruction.POP)
        .PushData(0).Op(Instruction.CALLDATALOAD).PushData(1).Op(Instruction.SSTORE)
        .CALLDATACOPY(64, 0, 32).PushData(64).Op(Instruction.MLOAD).PushData(2).Op(Instruction.SSTORE)
        .STOP();

    private byte[] ReadStorage(Address address, int slot)
    {
        TestState.Get(new StorageCell(address, (UInt256)slot), out UInt256 value);
        return value.ToBigEndian();
    }

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
