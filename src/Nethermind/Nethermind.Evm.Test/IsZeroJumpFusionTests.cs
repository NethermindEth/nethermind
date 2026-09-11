// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// VM-level regression tests for the ISZERO+PUSH2+JUMPI peephole optimization in
/// <c>InstructionIsZero</c>. The fused path branches on ISZERO's operand without writing the
/// boolean the jump would have read back, so the branch is taken when the operand is zero -
/// the inverse of the condition the unfused <c>PUSH2</c> path tests. It also takes over the
/// gas, counting, and stack-limit duties of the two opcodes it swallows. Getting the
/// direction, the fault order, or the lookahead guard wrong would diverge from consensus on
/// virtually every non-trivial contract, so both branch outcomes, the sequences that must not
/// fuse, and each fault path are exercised explicitly.
/// </summary>
[Parallelizable(ParallelScope.Self)]
public class IsZeroJumpFusionTests : VirtualMachineTestsBase
{
    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.CancunBlockTimestamp;

    // The fusion is gated on `!TTracingInst.IsActive`. The default TestAllTracerWithOutput
    // reports instruction tracing and would select the unfused specialization, so use a tracer
    // that still captures GasSpent but asks for no instruction-level trace.
    protected override TestAllTracerWithOutput CreateTracer() => new NoInstructionTracer();

    private sealed class NoInstructionTracer : TestAllTracerWithOutput
    {
        public override bool IsTracingInstructions => false;
    }

    [Test]
    public void Zero_operand_takes_the_branch()
    {
        // A zero operand is what ISZERO turns into a truthy condition, so the jump is taken.
        //   [0] PUSH1 0x00       (operand = 0)
        //   [2] ISZERO           (fused with the PUSH2 and JUMPI that follow)
        //   [3] PUSH2 0x000A
        //   [6] JUMPI
        //   [7] PUSH1 0xFF       (fall-through, MUST NOT execute)
        //   [9] STOP
        //   [10] JUMPDEST        <- jump target
        //   [11] PUSH1 0x42
        //   [13] PUSH1 0x00
        //   [15] SSTORE
        //   [16] STOP
        byte[] dest = [0x00, 0x0A];
        byte[] code = Prepare.EvmCode
            .PushData((byte)0x00)
            .Op(Instruction.ISZERO)
            .PushData(dest)
            .Op(Instruction.JUMPI)
            .PushData((byte)0xFF)
            .Op(Instruction.STOP)
            .Op(Instruction.JUMPDEST)
            .PushData((byte)0x42)
            .PushData((byte)0x00)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput r = Execute(code);
        AssertStorage(0, (UInt256)0x42);
        // 21000 intrinsic + PUSH1 3 + ISZERO 3 + PUSH2 3 + JUMPI 10 + JUMPDEST 1 + PUSH1 3
        // + PUSH1 3 + SSTORE 20000. The pinned total catches a JUMPDEST charged twice or not
        // at all, which is how a counter left on the marker would show up.
        AssertGas(r, 41026);
    }

    [Test]
    public void Non_zero_operand_falls_through()
    {
        //   [0] PUSH1 0x01       (operand != 0, so ISZERO is false and the jump is not taken)
        //   [2] ISZERO
        //   [3] PUSH2 0x000D
        //   [6] JUMPI
        //   [7] PUSH1 0x11       (fall-through, MUST execute)
        //   [9] PUSH1 0x00
        //   [11] SSTORE
        //   [12] STOP
        //   [13] JUMPDEST
        //   [14] PUSH1 0x22      (MUST NOT execute)
        //   [16] PUSH1 0x00
        //   [18] SSTORE
        //   [19] STOP
        byte[] dest = [0x00, 0x0D];
        byte[] code = Prepare.EvmCode
            .PushData((byte)0x01)
            .Op(Instruction.ISZERO)
            .PushData(dest)
            .Op(Instruction.JUMPI)
            .PushData((byte)0x11)
            .PushData((byte)0x00)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Op(Instruction.JUMPDEST)
            .PushData((byte)0x22)
            .PushData((byte)0x00)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput r = Execute(code);
        AssertStorage(0, (UInt256)0x11);
        AssertGas(r, 41025);
    }

    [Test]
    public void Taken_branch_to_an_invalid_destination_faults()
    {
        // Offset 8 holds a PUSH1 opcode, not a JUMPDEST.
        //   [0] PUSH1 0x00
        //   [2] ISZERO
        //   [3] PUSH2 0x0008
        //   [6] JUMPI
        //   [7] STOP
        //   [8] PUSH1 0x99       (would run if the invalid jump somehow landed)
        //   [10] PUSH1 0x00
        //   [12] SSTORE
        //   [13] STOP
        byte[] dest = [0x00, 0x08];
        byte[] code = Prepare.EvmCode
            .PushData((byte)0x00)
            .Op(Instruction.ISZERO)
            .PushData(dest)
            .Op(Instruction.JUMPI)
            .Op(Instruction.STOP)
            .PushData((byte)0x99)
            .PushData((byte)0x00)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput r = Execute(code);
        AssertStorage(0, (UInt256)0);
        // InvalidJumpDestination consumes all remaining gas per EVM spec.
        AssertGas(r, 100000);
    }

    [Test]
    public void Untaken_branch_never_validates_the_destination()
    {
        // Same invalid destination as above, but the operand is non-zero so the jump is not
        // taken and the destination must never be looked at.
        //   [0] PUSH1 0x01
        //   [2] ISZERO
        //   [3] PUSH2 0x0009     (offset 9 is a PUSH1 opcode, not a JUMPDEST)
        //   [6] JUMPI
        //   [7] PUSH1 0x33
        //   [9] PUSH1 0x00
        //   [11] SSTORE
        //   [12] STOP
        byte[] dest = [0x00, 0x09];
        byte[] code = Prepare.EvmCode
            .PushData((byte)0x01)
            .Op(Instruction.ISZERO)
            .PushData(dest)
            .Op(Instruction.JUMPI)
            .PushData((byte)0x33)
            .PushData((byte)0x00)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput r = Execute(code);
        AssertStorage(0, (UInt256)0x33);
        AssertGas(r, 41025);
    }

    [Test]
    public void Unfused_ISZERO_still_leaves_its_result_on_the_stack()
    {
        //   [0] PUSH1 0x00
        //   [2] ISZERO           (no PUSH2 follows, so the result has to be materialized)
        //   [3] PUSH1 0x00
        //   [5] SSTORE           (stores the ISZERO result under key 0)
        //   [6] STOP
        byte[] code = Prepare.EvmCode
            .PushData((byte)0x00)
            .Op(Instruction.ISZERO)
            .PushData((byte)0x00)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput r = Execute(code);
        AssertStorage(0, (UInt256)1);
        AssertGas(r, 41009);
    }

    [Test]
    public void ISZERO_before_an_unconditional_jump_does_not_fuse()
    {
        // PUSH2+JUMP still fuses on its own, but ISZERO must not join it: the unconditional
        // jump does not consume the condition, so the result stays on the stack.
        //   [0] PUSH1 0x00
        //   [2] ISZERO
        //   [3] PUSH2 0x0007
        //   [6] JUMP
        //   [7] JUMPDEST
        //   [8] PUSH1 0x00
        //   [10] SSTORE          (stores the ISZERO result under key 0)
        //   [11] STOP
        byte[] dest = [0x00, 0x07];
        byte[] code = Prepare.EvmCode
            .PushData((byte)0x00)
            .Op(Instruction.ISZERO)
            .PushData(dest)
            .Op(Instruction.JUMP)
            .Op(Instruction.JUMPDEST)
            .PushData((byte)0x00)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput r = Execute(code);
        AssertStorage(0, (UInt256)1);
        AssertGas(r, 41021);
    }

    [Test]
    public void ISZERO_before_a_truncated_PUSH2_keeps_the_implicit_stop()
    {
        // The JUMPI the lookahead needs is past the end of the code, so nothing fuses and the
        // trailing PUSH2 falls into the implicit STOP that ends a frame on incomplete data.
        //   [0] PUSH1 0x42
        //   [2] PUSH1 0x00
        //   [4] SSTORE
        //   [5] PUSH1 0x00
        //   [7] ISZERO
        //   [8] PUSH2 0x0000     (code ends after the immediate)
        byte[] truncated = [0x00, 0x00];
        byte[] code = Prepare.EvmCode
            .PushData((byte)0x42)
            .PushData((byte)0x00)
            .Op(Instruction.SSTORE)
            .PushData((byte)0x00)
            .Op(Instruction.ISZERO)
            .PushData(truncated)
            .Done;

        TestAllTracerWithOutput r = Execute(code);
        AssertStorage(0, (UInt256)0x42);
        AssertGas(r, 41015);
    }

    [Test]
    public void ISZERO_on_an_empty_stack_underflows()
    {
        byte[] code = Prepare.EvmCode
            .Op(Instruction.ISZERO)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput r = Execute(code);
        AssertGas(r, 100000);
    }

    [Test]
    public void A_full_stack_overflows_at_the_fused_PUSH2()
    {
        // The fused sequence is one word shorter on the way out than on the way in, but the
        // PUSH2 it swallows still runs against a full stack and has to fault there.
        byte[] dest = [0x00, 0x00];
        Prepare prepare = Prepare.EvmCode;
        for (int i = 0; i < EvmStack.MaxStackSize - 1; i++)
            prepare = prepare.PushData((byte)0x00);

        byte[] code = prepare
            .Op(Instruction.ISZERO)
            .PushData(dest)
            .Op(Instruction.JUMPI)
            .Op(Instruction.JUMPDEST)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput r = Execute(code);
        // StackOverflow consumes all remaining gas, as every EVM fault but a revert does.
        AssertGas(r, 100000);
    }
}
