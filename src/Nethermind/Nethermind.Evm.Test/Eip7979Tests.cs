// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Tests for EIP-7979: CALLSUB, ENTERSUB, and RETURNSUB subroutine opcodes.
/// </summary>
[TestFixture]
public class Eip7979Tests : VirtualMachineTestsBase
{
    protected override ISpecProvider SpecProvider { get; } =
        new TestSpecProvider(new OverridableReleaseSpec(Prague.Instance) { IsEip7979Enabled = true });

    [Test]
    public void CallSub_executes_subroutine_and_returns()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, 0x07,      // 0..1: subroutine offset
            (byte)Instruction.CALLSUB,          // 2: jumps to 7, pushes 3
            (byte)Instruction.PUSH1, 0x00,      // 3..4: MSTORE offset
            (byte)Instruction.MSTORE,           // 5: stores subroutine result at 0
            (byte)Instruction.STOP,             // 6
            (byte)Instruction.ENTERSUB,         // 7: subroutine entry
            (byte)Instruction.PUSH1, 0x2a,      // 8..9: result
            (byte)Instruction.RETURNSUB,        // 10: returns to 3
        ];

        TestAllTracerWithOutput result = Execute(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            // Total: PUSH1(3) + CALLSUB(8) + ENTERSUB(1) + PUSH1(3) + RETURNSUB(5) + PUSH1(3) + MSTORE(3+3 memory)
            Assert.That(result.GasSpent, Is.EqualTo(GasCostOf.Transaction
                + GasCostOf.VeryLow + GasCostOf.Mid + GasCostOf.JumpDest + GasCostOf.VeryLow
                + GasCostOf.Low + GasCostOf.VeryLow + GasCostOf.VeryLow + GasCostOf.Memory));
        }
    }

    [Test]
    public void Nested_subroutine_calls_return_in_order()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, 0x05,      // outer subroutine at 5
            (byte)Instruction.CALLSUB,          // 2: pushes 3
            (byte)Instruction.STOP,             // 3: final return lands here
            (byte)Instruction.STOP,             // 4: padding
            (byte)Instruction.ENTERSUB,         // 5: outer entry
            (byte)Instruction.PUSH1, 0x0b,      // 6..7: inner subroutine at 11
            (byte)Instruction.CALLSUB,          // 8: pushes 9
            (byte)Instruction.RETURNSUB,        // 9: returns to 3 (STOP)
            (byte)Instruction.STOP,             // 10
            (byte)Instruction.ENTERSUB,         // 11: inner entry
            (byte)Instruction.RETURNSUB,        // 12: returns to 9
        ];

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
    }

    [Test]
    public void CallSub_to_non_entersub_destination_halts()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, 0x04,
            (byte)Instruction.CALLSUB,
            (byte)Instruction.STOP,
            (byte)Instruction.JUMPDEST,         // 4: not an ENTERSUB
        ];

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Failure));
    }

    [Test]
    public void CallSub_into_push_data_halts()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, 0x04,
            (byte)Instruction.CALLSUB,
            (byte)Instruction.PUSH1, (byte)Instruction.ENTERSUB, // 4: ENTERSUB byte inside push data
            (byte)Instruction.STOP,
        ];

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Failure));
    }

    [Test]
    public void ReturnSub_with_empty_return_stack_halts()
    {
        byte[] code = [(byte)Instruction.RETURNSUB];

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Failure));
    }

    [Test]
    public void EnterSub_reached_by_fallthrough_is_a_noop()
    {
        byte[] code =
        [
            (byte)Instruction.ENTERSUB,
            (byte)Instruction.STOP,
        ];

        TestAllTracerWithOutput result = Execute(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(result.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.JumpDest));
        }
    }

}
