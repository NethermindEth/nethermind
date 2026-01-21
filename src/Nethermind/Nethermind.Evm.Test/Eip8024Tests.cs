// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
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

    protected override ISpecProvider SpecProvider => new TestSpecProvider(new Osaka() { IsEip8024Enabled = true });

    [Test]
    public void DupN_ValidImmediate_DuplicatesStackElement()
    {
        // Push values 1-20 onto the stack, then DUPN with immediate 0x00 (depth=17)
        // Should duplicate the 17th element from top
        byte[] code = Prepare.EvmCode
            .PushData(1).PushData(2).PushData(3).PushData(4).PushData(5)
            .PushData(6).PushData(7).PushData(8).PushData(9).PushData(10)
            .PushData(11).PushData(12).PushData(13).PushData(14).PushData(15)
            .PushData(16).PushData(17).PushData(18).PushData(19).PushData(20)
            .Op(Instruction.DUPN).Data(0x00) // DUPN with immediate 0x00 -> depth=17
            .MSTORE(0)
            .Return(32, 0)
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Success);

        // Stack: [1, 2, 3, 4, 5, ..., 20] with 20 on top (20 items)
        // Position 17 from top = index 20-17 = 3 = value 4
        // After DUPN: top = 4
        new UInt256(result.ReturnValue, true).Should().Be(4);
    }

    [Test]
    public void DupN_DisallowedImmediate_Fails()
    {
        // DUPN with disallowed immediate 0x5b
        byte[] code = Prepare.EvmCode
            .PushData(1).PushData(2).PushData(3).PushData(4).PushData(5)
            .PushData(6).PushData(7).PushData(8).PushData(9).PushData(10)
            .PushData(11).PushData(12).PushData(13).PushData(14).PushData(15)
            .PushData(16).PushData(17).PushData(18).PushData(19).PushData(20)
            .Op(Instruction.DUPN).Data(0x5b) // Disallowed immediate
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Failure);
    }

    [Test]
    public void DupN_StackUnderflow_Fails()
    {
        // DUPN with immediate 0x00 (depth=17) but only 10 elements on stack
        byte[] code = Prepare.EvmCode
            .PushData(1).PushData(2).PushData(3).PushData(4).PushData(5)
            .PushData(6).PushData(7).PushData(8).PushData(9).PushData(10)
            .Op(Instruction.DUPN).Data(0x00) // depth=17, but only 10 elements
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Failure);
    }

    [Test]
    public void SwapN_ValidImmediate_SwapsStackElements()
    {
        // Push values, then SWAPN with immediate 0x00 (depth=17)
        // Should swap top with 17th element from top
        byte[] code = Prepare.EvmCode
            .PushData(1).PushData(2).PushData(3).PushData(4).PushData(5)
            .PushData(6).PushData(7).PushData(8).PushData(9).PushData(10)
            .PushData(11).PushData(12).PushData(13).PushData(14).PushData(15)
            .PushData(16).PushData(17).PushData(18).PushData(19).PushData(20)
            .Op(Instruction.SWAPN).Data(0x00) // SWAPN with immediate 0x00 -> depth=17
            .MSTORE(0) // Store new top
            .Return(32, 0)
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Success);

        // Stack: [1, 2, 3, 4, 5, ..., 20] with 20 on top (20 items)
        // SWAPN 17: swap top (20) with position 17 from top (index 3 = value 4)
        // After swap: top = 4
        new UInt256(result.ReturnValue, true).Should().Be(4);
    }

    [Test]
    public void SwapN_DisallowedImmediate_Fails()
    {
        // SWAPN with disallowed immediate 0x7f
        byte[] code = Prepare.EvmCode
            .PushData(1).PushData(2).PushData(3).PushData(4).PushData(5)
            .PushData(6).PushData(7).PushData(8).PushData(9).PushData(10)
            .PushData(11).PushData(12).PushData(13).PushData(14).PushData(15)
            .PushData(16).PushData(17).PushData(18).PushData(19).PushData(20)
            .Op(Instruction.SWAPN).Data(0x7f) // Disallowed immediate
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Failure);
    }

    [Test]
    public void SwapN_StackUnderflow_Fails()
    {
        // SWAPN with immediate 0x00 (depth=17) but only 10 elements
        byte[] code = Prepare.EvmCode
            .PushData(1).PushData(2).PushData(3).PushData(4).PushData(5)
            .PushData(6).PushData(7).PushData(8).PushData(9).PushData(10)
            .Op(Instruction.SWAPN).Data(0x00)
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Failure);
    }

    [Test]
    public void Exchange_ValidImmediate_ExchangesStackElements()
    {
        // Push values, then EXCHANGE with immediate 0x12 -> decode_pair gives (2, 3)
        // Exchange positions 2 and 3 from top
        byte[] code = Prepare.EvmCode
            .PushData(1).PushData(2).PushData(3).PushData(4).PushData(5)
            .Op(Instruction.EXCHANGE).Data(0x12) // n=2, m=3 -> exchange positions 2 and 3
            .MSTORE(0) // Store top
            .Return(32, 0)
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Success);

        // Stack before EXCHANGE: [1, 2, 3, 4, 5] (5 on top)
        // decode_pair(0x12) returns n=2, m=3
        // EXCHANGE swaps positions 3 and 4 from top (values 3 and 2)
        // After EXCHANGE: stack = [1, 3, 2, 4, 5] (5 still on top)
        // MSTORE stores 5 at offset 0
        new UInt256(result.ReturnValue, true).Should().Be(5);
    }

    [Test]
    public void Exchange_DisallowedImmediate_Fails()
    {
        // EXCHANGE with disallowed immediate 0x50
        byte[] code = Prepare.EvmCode
            .PushData(1).PushData(2).PushData(3).PushData(4).PushData(5)
            .Op(Instruction.EXCHANGE).Data(0x50) // Disallowed immediate
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Failure);
    }

    [Test]
    public void Exchange_DisallowedRange_AllFail()
    {
        // Full disallowed range 0x50-0x7f
        for (byte imm = 0x50; imm <= 0x7f; imm++)
        {
            Prepare prepare = Prepare.EvmCode;
            for (int i = 0; i < 32; i++) prepare.PushData(i);
            byte[] code = prepare.Op(Instruction.EXCHANGE).Data(imm).Done;

            TestAllTracerWithOutput result = Execute(code);
            result.StatusCode.Should().Be(StatusCode.Failure, $"Immediate 0x{imm:X2} should fail");
        }
    }

    [Test]
    public void Exchange_HighRangeImmediate_Succeeds()
    {
        // Test 0xd0: k=160, q=10, r=0, q>=r -> n=r+2=2, m=(29-q)+2=21
        // Exchange(2, 21) needs 21 items on stack
        Prepare prepare = Prepare.EvmCode;
        for (int i = 1; i <= 21; i++) prepare.PushData(i);
        byte[] code = prepare
            .Op(Instruction.EXCHANGE).Data(0xd0)
            .MSTORE(0).Return(32, 0)
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Success);
        // Stack top is 21, which is stored
        new UInt256(result.ReturnValue, true).Should().Be(21);
    }

    [Test]
    public void Exchange_EdgeCase_0x4f_Succeeds()
    {
        // 0x4f: k=79, q=4, r=15, q<r -> (5, 16) -> Exchange(6, 17)
        // Need 17 items on stack
        Prepare prepare = Prepare.EvmCode;
        for (int i = 1; i <= 17; i++) prepare.PushData(i);
        byte[] code = prepare
            .Op(Instruction.EXCHANGE).Data(0x4f)
            .MSTORE(0).Return(32, 0)
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Success);
    }

    [Test]
    public void Exchange_EdgeCase_0x80_Succeeds()
    {
        // 0x80: k=80 (128-48), q=5, r=0, q>=r -> n=r+2=2, m=(29-5)+2=26
        // Exchange(2, 26) needs 26 items on stack
        Prepare prepare = Prepare.EvmCode;
        for (int i = 1; i <= 26; i++) prepare.PushData(i);
        byte[] code = prepare
            .Op(Instruction.EXCHANGE).Data(0x80)
            .MSTORE(0).Return(32, 0)
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Success);
    }

    [Test]
    public void Exchange_StackUnderflow_Fails()
    {
        // EXCHANGE with immediate 0x12 -> decode_pair gives (2, 3)
        // Exchange(n+1, m+1) = Exchange(3, 4) requires at least 4 items on stack
        byte[] code = Prepare.EvmCode
            .PushData(1).PushData(2)
            .Op(Instruction.EXCHANGE).Data(0x12) // n=2, m=3 -> needs at least 4 elements
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Failure);
    }

    [Test]
    public void DupN_MissingImmediateAtEnd_Fails()
    {
        byte[] code = Prepare.EvmCode
            .Op(Instruction.DUPN)
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Failure);
    }

    [Test]
    public void SwapN_MissingImmediateAtEnd_Fails()
    {
        byte[] code = Prepare.EvmCode
            .Op(Instruction.SWAPN)
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Failure);
    }

    [Test]
    public void Exchange_MissingImmediateAtEnd_Fails()
    {
        byte[] code = Prepare.EvmCode
            .Op(Instruction.EXCHANGE)
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Failure);
    }

    [Test]
    public void DupN_MaxDepth_StackUnderflow()
    {
        Prepare prepare = Prepare.EvmCode;
        for (int i = 0; i < 234; i++)
        {
            prepare.PushData(0);
        }

        byte[] code = prepare
            .Op(Instruction.DUPN).Data(0xff) // depth=235
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Failure);
    }

    [Test]
    public void SwapN_MaxDepth_StackUnderflow()
    {
        Prepare prepare = Prepare.EvmCode;
        for (int i = 0; i < 234; i++)
        {
            prepare.PushData(0);
        }

        byte[] code = prepare
            .Op(Instruction.SWAPN).Data(0xff) // depth=235
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Failure);
    }

    [Test]
    public void Exchange_MaxDepth_StackUnderflow()
    {
        Prepare prepare = Prepare.EvmCode;
        for (int i = 0; i < 29; i++)
        {
            prepare.PushData(0);
        }

        byte[] code = prepare
            .Op(Instruction.EXCHANGE).Data(0x00) // n=1, m=29 -> needs depth 30
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Failure);
    }

    [Test]
    public void DupN_CostsVeryLowGas()
    {
        // Gas cost should be 3 (VeryLow)
        const long expectedGas = GasCostOf.Transaction + GasCostOf.VeryLow * 20 + GasCostOf.VeryLow; // 20 PUSH + DUPN

        byte[] code = Prepare.EvmCode
            .PushData(1).PushData(2).PushData(3).PushData(4).PushData(5)
            .PushData(6).PushData(7).PushData(8).PushData(9).PushData(10)
            .PushData(11).PushData(12).PushData(13).PushData(14).PushData(15)
            .PushData(16).PushData(17).PushData(18).PushData(19).PushData(20)
            .Op(Instruction.DUPN).Data(0x00)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput result = Execute(Activation, 100000, code);
        result.StatusCode.Should().Be(StatusCode.Success);
        AssertGas(result, expectedGas);
    }

    [Test]
    public void SwapN_CostsVeryLowGas()
    {
        // Gas cost should be 3 (VeryLow)
        const long expectedGas = GasCostOf.Transaction + GasCostOf.VeryLow * 20 + GasCostOf.VeryLow; // 20 PUSH + SWAPN

        byte[] code = Prepare.EvmCode
            .PushData(1).PushData(2).PushData(3).PushData(4).PushData(5)
            .PushData(6).PushData(7).PushData(8).PushData(9).PushData(10)
            .PushData(11).PushData(12).PushData(13).PushData(14).PushData(15)
            .PushData(16).PushData(17).PushData(18).PushData(19).PushData(20)
            .Op(Instruction.SWAPN).Data(0x00)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput result = Execute(Activation, 100000, code);
        result.StatusCode.Should().Be(StatusCode.Success);
        AssertGas(result, expectedGas);
    }

    [Test]
    public void Exchange_CostsVeryLowGas()
    {
        // Gas cost should be 3 (VeryLow)
        const long expectedGas = GasCostOf.Transaction + GasCostOf.VeryLow * 5 + GasCostOf.VeryLow; // 5 PUSH + EXCHANGE

        byte[] code = Prepare.EvmCode
            .PushData(1).PushData(2).PushData(3).PushData(4).PushData(5)
            .Op(Instruction.EXCHANGE).Data(0x12) // decode_pair(0x12) = (2, 3)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput result = Execute(Activation, 100000, code);
        result.StatusCode.Should().Be(StatusCode.Success);
        AssertGas(result, expectedGas);
    }

    [Test]
    public void EipTestVector_DupN_18Items_TopIs1()
    {
        // EIP test: 60016000808080808080808080808080808080e600
        // PUSH1 01, PUSH1 00, DUP1 x15, DUPN 0x00
        // Note: The bytecode has 15 DUP1s (30 hex chars = 15 bytes)
        // After setup: stack = [1, 0, 0, ...] (17 items, 1 at bottom)
        // DUPN with decode(0)=17: duplicate 17th item from top = bottom item = 1
        // Result: 18 items with top = 1
        byte[] code = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1)
            .Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1)
            .Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1)
            .Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1) // 15 DUP1s total
            .Op(Instruction.DUPN).Data(0x00)
            .MSTORE(0)
            .Return(32, 0)
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Success);
        new UInt256(result.ReturnValue, true).Should().Be(1);
    }

    [Test]
    public void EipTestVector_SwapN_18Items_TopIs0()
    {
        // EIP test: 600160008080808080808080808080808080806002e700
        // PUSH1 01, PUSH1 00, DUP1 x15, PUSH1 02, SWAPN 0x00
        // After setup: stack = [1, 0, 0, ..., 0, 2] (18 items with 2 on top, 1 at bottom)
        // SWAPN 17: swap top (2) with position 17 from top (value 0)
        // Result: top = 0
        byte[] code = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1)
            .Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1)
            .Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1)
            .Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1) // 15 DUP1s total
            .PushData(2)
            .Op(Instruction.SWAPN).Data(0x00) // SWAPN with depth=17
            .MSTORE(0)
            .Return(32, 0)
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Success);
        // After SWAPN 17: swap top (2) with position 17 from top = 0
        new UInt256(result.ReturnValue, true).Should().Be(0);
    }

    [Test]
    public void EipTestVector_Exchange_3Items()
    {
        // EIP test: 600060016002e801
        // PUSH1 00, PUSH1 01, PUSH1 02, EXCHANGE 0x01
        // Stack before: [0, 1, 2] (2 on top)
        // decode_pair(0x01): k=1, q=0, r=1, q<r -> (1, 2)
        // Exchange positions (n+1) and (m+1) = positions 2 and 3 from top.
        // After: [1, 0, 2] (2 on top), matching the EIP vector.
        byte[] code = Prepare.EvmCode
            .PushData(0)
            .PushData(1)
            .PushData(2)
            .Op(Instruction.EXCHANGE).Data(0x01)
            .MSTORE(0)
            .Return(32, 0)
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Success);
        new UInt256(result.ReturnValue, true).Should().Be(2);
    }

    [Test]
    public void EipTestVector_InvalidSwapn_Reverts()
    {
        // EIP test: e75b - SWAPN with disallowed immediate 0x5b causes revert
        byte[] code = new byte[] { 0xe7, 0x5b };

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Failure);
    }

    [Test]
    public void EipTestVector_JumpOverInvalidDupn_Succeeds()
    {
        // EIP test: 600456e65b
        // PUSH1 04, JUMP, INVALID_DUPN (e6), JUMPDEST (5b)
        // The JUMP skips over the invalid DUPN to land on JUMPDEST
        // This tests that e65b is properly parsed as INVALID_DUPN followed by JUMPDEST
        byte[] code = new byte[] { 0x60, 0x04, 0x56, 0xe6, 0x5b, 0x00 }; // Added STOP at end

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Success);
    }

    [Test]
    public void EipTestVector_DupN_StackUnderflow_ExceptionalHalt()
    {
        // EIP test: 6000808080808080808080808080808080e600
        // PUSH1 00, DUP1 x15, DUPN 0x00
        // Stack has 16 items, DUPN 17 needs depth 17 -> exceptional halt.
        byte[] code = Prepare.EvmCode
            .PushData(0)
            .Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1)
            .Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1)
            .Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1)
            .Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1) // 15 DUP1s total
            .Op(Instruction.DUPN).Data(0x00)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Failure);
    }

    [Test]
    public void EipTestVector_DupN_16Items_StackUnderflow()
    {
        // Test actual underflow: 16 items but need depth 17
        byte[] code = Prepare.EvmCode
            .PushData(0)
            .Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1)
            .Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1)
            .Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1)
            .Op(Instruction.DUP1).Op(Instruction.DUP1).Op(Instruction.DUP1) // Only 15 DUP1s = 16 items
            .Op(Instruction.DUPN).Data(0x00) // depth=17, but only 16 items
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        result.StatusCode.Should().Be(StatusCode.Failure);
    }

    /// <summary>
    /// Test class with EIP-8024 disabled to verify opcodes fail when not enabled.
    /// </summary>
    public class Eip8024DisabledTests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
        protected override ulong Timestamp => MainnetSpecProvider.OsakaBlockTimestamp;

        // EIP-8024 is NOT enabled in this test class
        protected override ISpecProvider SpecProvider => new TestSpecProvider(new Osaka() { IsEip8024Enabled = false });

        [Test]
        public void DupN_WhenDisabled_Fails()
        {
            byte[] code = Prepare.EvmCode
                .PushData(1).PushData(2).PushData(3).PushData(4).PushData(5)
                .PushData(6).PushData(7).PushData(8).PushData(9).PushData(10)
                .PushData(11).PushData(12).PushData(13).PushData(14).PushData(15)
                .PushData(16).PushData(17).PushData(18).PushData(19).PushData(20)
                .Op(Instruction.DUPN).Data(0x00)
                .Done;

            TestAllTracerWithOutput result = Execute(code);

            // The DUPN opcode should fail as a bad instruction when EIP-8024 is not enabled
            result.StatusCode.Should().Be(StatusCode.Failure);
        }

        [Test]
        public void SwapN_WhenDisabled_Fails()
        {
            byte[] code = Prepare.EvmCode
                .PushData(1).PushData(2).PushData(3).PushData(4).PushData(5)
                .PushData(6).PushData(7).PushData(8).PushData(9).PushData(10)
                .PushData(11).PushData(12).PushData(13).PushData(14).PushData(15)
                .PushData(16).PushData(17).PushData(18).PushData(19).PushData(20)
                .Op(Instruction.SWAPN).Data(0x00)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            result.StatusCode.Should().Be(StatusCode.Failure);
        }

        [Test]
        public void Exchange_WhenDisabled_Fails()
        {
            byte[] code = Prepare.EvmCode
                .PushData(1).PushData(2).PushData(3).PushData(4).PushData(5)
                .Op(Instruction.EXCHANGE).Data(0x12) // decode_pair(0x12) = (2, 3)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            result.StatusCode.Should().Be(StatusCode.Failure);
        }
    }
}
