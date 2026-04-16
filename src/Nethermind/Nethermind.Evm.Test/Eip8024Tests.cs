// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Tests for EIP-8024: Backward-compatible SWAPN, DUPN, EXCHANGE for legacy code.
/// </summary>
public class Eip8024Tests : VirtualMachineTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.OsakaBlockTimestamp;
    protected override ISpecProvider SpecProvider => new TestSpecProvider(new Osaka { IsEip8024Enabled = true });

    private static Prepare PushNValues(int count) => Prepare.EvmCode.For(count, static (p, i) => p.PushData(i + 1));
    private static Prepare PushZeros(int count) => Prepare.EvmCode.For(count, static (p, _) => p.PushData(0));

    private static IEnumerable<TestCaseData> SuccessTestCases()
    {
        // DUPN valid: 20 items, depth=17, position 17 from top = value 4
        yield return new TestCaseData(PushNValues(20).Op(Instruction.DUPN).Data(0x80).MSTORE(0).Return(32, 0).Done, 4).SetName("DupN_ValidImmediate");

        // SWAPN valid: 20 items, depth=17, swap top (20) with 18th from top (3)
        yield return new TestCaseData(PushNValues(20).Op(Instruction.SWAPN).Data(0x80).MSTORE(0).Return(32, 0).Done, 3).SetName("SwapN_ValidImmediate");

        // Exchange valid: 5 items, (n=3,m=4) swaps 1-based stack positions 3 and 4, top unchanged
        yield return new TestCaseData(PushNValues(5).Op(Instruction.EXCHANGE).Data(0x9d).MSTORE(0).Return(32, 0).Done, 5).SetName("Exchange_ValidImmediate");

        // Exchange edge cases: newly valid 0x50 (was disallowed in old range 80-127)
        yield return new TestCaseData(PushNValues(17).Op(Instruction.EXCHANGE).Data(0x50).MSTORE(0).Return(32, 0).Done, 17).SetName("Exchange_NewlyValid_0x50");

        // Exchange edge cases: newly valid 0x51
        yield return new TestCaseData(PushNValues(16).Op(Instruction.EXCHANGE).Data(0x51).MSTORE(0).Return(32, 0).Done, 16).SetName("Exchange_NewlyValid_0x51");

        // Exchange high range: 0x2f -> k=0xa0, (n=2,m=20) 1-indexed, needs 20 items
        yield return new TestCaseData(PushNValues(20).Op(Instruction.EXCHANGE).Data(0x2f).MSTORE(0).Return(32, 0).Done, 20).SetName("Exchange_HighRange_0x2f");

        // Exchange edge: 0xC0 -> k=0x4f=79, (n=6,m=17) 1-indexed, needs 17 items
        yield return new TestCaseData(PushNValues(17).Op(Instruction.EXCHANGE).Data(0xC0).MSTORE(0).Return(32, 0).Done, 17).SetName("Exchange_EdgeCase_0xC0");

        // Exchange edge: 0xDF -> k=0x50=80, (n=2,m=25) 1-indexed, needs 25 items
        yield return new TestCaseData(PushNValues(25).Op(Instruction.EXCHANGE).Data(0xDF).MSTORE(0).Return(32, 0).Done, 25).SetName("Exchange_EdgeCase_0xDF");

        // EIP test vector: PUSH1 1, PUSH1 0, DUP1 x15, DUPN 0x80 -> duplicates bottom item (1)
        yield return new TestCaseData(Prepare.EvmCode.PushData(1).PushData(0).Dup1Chain(15).Op(Instruction.DUPN).Data(0x80).MSTORE(0).Return(32, 0).Done, 1).SetName("EipTestVector_DupN_18Items");

        // EIP test vector: PUSH1 1, PUSH1 0, DUP1 x15, PUSH1 2, SWAPN 0x80 -> swap top (2) with bottom (1)
        yield return new TestCaseData(Prepare.EvmCode.PushData(1).PushData(0).Dup1Chain(15).PushData(2).Op(Instruction.SWAPN).Data(0x80).MSTORE(0).Return(32, 0).Done, 1).SetName("EipTestVector_SwapN_18Items");

        // EIP test vector: PUSH1 0, PUSH1 1, PUSH1 2, EXCHANGE 0x8e -> swaps positions 1,2, top stays 2
        yield return new TestCaseData(Prepare.EvmCode.PushData(0).PushData(1).PushData(2).Op(Instruction.EXCHANGE).Data(0x8e).MSTORE(0).Return(32, 0).Done, 2).SetName("EipTestVector_Exchange_3Items");
    }

    [TestCaseSource(nameof(SuccessTestCases))]
    public void ValidOperation_Succeeds(byte[] code, int expectedReturn)
    {
        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Success);
        new UInt256(result.ReturnValue, true).Should().Be((UInt256)expectedReturn);
    }

    private static IEnumerable<TestCaseData> MissingImmediateSuccessTestCases()
    {
        yield return new TestCaseData(PushNValues(145).Op(Instruction.DUPN).Done).SetName("DupN_MissingImmediate_DecodesAsZero");
        yield return new TestCaseData(PushNValues(146).Op(Instruction.SWAPN).Done).SetName("SwapN_MissingImmediate_DecodesAsZero");
        yield return new TestCaseData(PushNValues(17).Op(Instruction.EXCHANGE).Done).SetName("Exchange_MissingImmediate_DecodesAsZero");
    }

    [TestCaseSource(nameof(MissingImmediateSuccessTestCases))]
    public void MissingImmediate_AtEndOfCode_DecodesAsZero(byte[] code)
    {
        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Success);
    }

    private static IEnumerable<TestCaseData> FailureTestCases()
    {
        // Disallowed immediates (91-127 for DUPN/SWAPN, 82-127 for EXCHANGE)
        yield return new TestCaseData(PushNValues(20).Op(Instruction.DUPN).Data(0x5b).Done).SetName("DupN_Disallowed_0x5b");
        yield return new TestCaseData(PushNValues(20).Op(Instruction.SWAPN).Data(0x7f).Done).SetName("SwapN_Disallowed_0x7f");
        yield return new TestCaseData(PushNValues(5).Op(Instruction.EXCHANGE).Data(0x52).Done).SetName("Exchange_Disallowed_0x52");

        // Stack underflow: depth exceeds available items
        yield return new TestCaseData(PushNValues(10).Op(Instruction.DUPN).Data(0x80).Done).SetName("DupN_StackUnderflow");
        yield return new TestCaseData(PushNValues(10).Op(Instruction.SWAPN).Data(0x80).Done).SetName("SwapN_StackUnderflow");
        yield return new TestCaseData(PushNValues(2).Op(Instruction.EXCHANGE).Data(0x9d).Done).SetName("Exchange_StackUnderflow");

        // Missing immediate at end of code decodes as 0, then fails if the stack is too shallow.
        yield return new TestCaseData(new byte[] { 0xe6 }).SetName("DupN_MissingImmediate");
        yield return new TestCaseData(new byte[] { 0xe7 }).SetName("SwapN_MissingImmediate");
        yield return new TestCaseData(new byte[] { 0xe8 }).SetName("Exchange_MissingImmediate");

        // Regression: PUSH1 0x42, DUPN with no immediate must underflow, not gracefully STOP.
        yield return new TestCaseData(new byte[] { 0x60, 0x42, 0xe6 }).SetName("DupN_TruncatedAfterPush");

        // Max depth: immediate 0x5a -> depth=235, only 234 items
        yield return new TestCaseData(PushZeros(234).Op(Instruction.DUPN).Data(0x5a).Done).SetName("DupN_MaxDepth_235");
        yield return new TestCaseData(PushZeros(234).Op(Instruction.SWAPN).Data(0x5a).Done).SetName("SwapN_MaxDepth_235");

        // Exchange max depth: immediate 0x8f -> (n=2,m=30) 1-indexed, needs 30 items, only 29
        yield return new TestCaseData(PushZeros(29).Op(Instruction.EXCHANGE).Data(0x8f).Done).SetName("Exchange_MaxDepth_30");

        // EIP test vector: SWAPN with disallowed immediate 0x5b
        yield return new TestCaseData(new byte[] { 0xe7, 0x5b }).SetName("EipTestVector_InvalidSwapn");

        // EIP test vector: 16 items (PUSH1 0 + DUP1 x15), DUPN depth=17 -> underflow
        yield return new TestCaseData(PushZeros(1).Dup1Chain(15).Op(Instruction.DUPN).Data(0x80).Op(Instruction.STOP).Done).SetName("EipTestVector_DupN_StackUnderflow");
    }

    [TestCaseSource(nameof(FailureTestCases))]
    public void InvalidOperation_Fails(byte[] code)
    {
        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Failure);
    }

    private static IEnumerable<TestCaseData> GasCostTestCases()
    {
        long Gas(int pushCount) => GasCostOf.Transaction + GasCostOf.VeryLow * pushCount + GasCostOf.VeryLow;

        yield return new TestCaseData(PushNValues(20).Op(Instruction.DUPN).Data(0x80).Op(Instruction.STOP).Done, Gas(20)).SetName("DupN_GasCost");
        yield return new TestCaseData(PushNValues(20).Op(Instruction.SWAPN).Data(0x80).Op(Instruction.STOP).Done, Gas(20)).SetName("SwapN_GasCost");
        yield return new TestCaseData(PushNValues(5).Op(Instruction.EXCHANGE).Data(0x9d).Op(Instruction.STOP).Done, Gas(5)).SetName("Exchange_GasCost");
    }

    [TestCaseSource(nameof(GasCostTestCases))]
    public void Opcode_CostsVeryLowGas(byte[] code, long expectedGas)
    {
        TestAllTracerWithOutput result = Execute(Activation, 100000, code);
        result.StatusCode.Should().Be(StatusCode.Success);
        AssertGas(result, expectedGas);
    }

    [Test]
    public void Exchange_DisallowedRange_AllFail()
    {
        for (byte imm = 0x52; imm <= 0x7f; imm++)
        {
            byte[] code = PushNValues(32).Op(Instruction.EXCHANGE).Data(imm).Done;
            TestAllTracerWithOutput result = Execute(code);
            result.StatusCode.Should().Be(StatusCode.Failure, $"Immediate 0x{imm:X2} should fail");
        }
    }

    [Test]
    public void EipTestVector_JumpOverInvalidDupn_Succeeds()
    {
        // PUSH1 04, JUMP, DUPN (e6), JUMPDEST (5b), STOP
        // JUMP skips over the invalid DUPN to land on JUMPDEST
        byte[] code = [0x60, 0x04, 0x56, 0xe6, 0x5b, 0x00];
        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Success);
    }

    public class Eip8024DisabledTests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
        protected override ulong Timestamp => MainnetSpecProvider.OsakaBlockTimestamp;
        protected override ISpecProvider SpecProvider => new TestSpecProvider(new Osaka() { IsEip8024Enabled = false });

        private static IEnumerable<TestCaseData> DisabledTestCases()
        {
            yield return new TestCaseData(Prepare.EvmCode.For(20, static (p, i) => p.PushData(i + 1)).Op(Instruction.DUPN).Data(0x80).Done).SetName("DupN_WhenDisabled");
            yield return new TestCaseData(Prepare.EvmCode.For(20, static (p, i) => p.PushData(i + 1)).Op(Instruction.SWAPN).Data(0x80).Done).SetName("SwapN_WhenDisabled");
            yield return new TestCaseData(Prepare.EvmCode.For(5, static (p, i) => p.PushData(i + 1)).Op(Instruction.EXCHANGE).Data(0x9d).Done).SetName("Exchange_WhenDisabled");
        }

        [TestCaseSource(nameof(DisabledTestCases))]
        public void Opcode_WhenDisabled_Fails(byte[] code)
        {
            TestAllTracerWithOutput result = Execute(code);
            result.StatusCode.Should().Be(StatusCode.Failure);
        }
    }
}

internal static class PrepareExtensions
{
    public static Prepare Dup1Chain(this Prepare prepare, int count) => prepare.For(count, static (p, _) => p.Op(Instruction.DUP1));
}
