// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// VM-level regression tests for the PUSH2+JUMP and PUSH2+JUMPI peephole optimization in
/// <c>InstructionPush2</c>. The fused path reads the 2-byte immediate, validates the
/// jump destination, charges JUMPDEST gas, and advances the program counter past the
/// destination - bypassing the normal PUSH then JUMP dispatch. A bug in the fused path
/// would diverge from consensus on virtually every non-trivial contract, so the three
/// outcomes (taken JUMP, taken JUMPI, not-taken JUMPI) and the invalid-destination
/// failure path must all be explicitly exercised.
/// </summary>
[Parallelizable(ParallelScope.Self)]
public class Push2JumpFusionTests : VirtualMachineTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.CancunBlockTimestamp;

    // The fusion path is gated on `!TTracingInst.IsActive`; using the default
    // TestAllTracerWithOutput (IsTracingInstructions = true) selects the OnFlag
    // specialization and bypasses the fusion. Override with a tracer that still
    // captures GasSpent but reports no instruction-level tracing so the JIT selects
    // the OffFlag path and the fused PUSH2+JUMP/JUMPI code is actually executed.
    protected override TestAllTracerWithOutput CreateTracer() => new NoInstructionTracer();

    private sealed class NoInstructionTracer : TestAllTracerWithOutput
    {
        public override bool IsTracingInstructions => false;
    }

    private static readonly byte[] Dest = [0x00, 0x07];

    [Test]
    public void PUSH2_JUMP_taken_to_valid_JUMPDEST_executes_after_destination()
    {
        // Code layout (offsets in brackets):
        //   [0] PUSH2 0x0007   (fused with following JUMP)
        //   [3] JUMP
        //   [4] INVALID        (skipped)
        //   [5] PUSH1 0xFF     (skipped - would break the assertion if executed)
        //   [7] JUMPDEST       <- jump target
        //   [8] PUSH1 0x42
        //   [10] PUSH1 0x00
        //   [12] SSTORE
        //   [13] STOP
        byte[] code = Prepare.EvmCode
            .PushData(Dest)
            .Op(Instruction.JUMP)
            .Op(Instruction.INVALID)
            .PushData((byte)0xFF)
            .Op(Instruction.JUMPDEST)
            .PushData((byte)0x42)
            .PushData((byte)0x00)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput r = Execute(code);
        AssertStorage(0, (UInt256)0x42);
        // Exact-gas assertion catches the PUSH2+JUMP fusion double-charge regression:
        // without the `programCounter++` past JUMPDEST, the dispatch loop re-executes the
        // JUMPDEST opcode and charges 1 extra gas.
        AssertGas(r, 41018);
    }

    [Test]
    public void PUSH2_JUMPI_taken_to_valid_JUMPDEST_executes_after_destination()
    {
        // Stack layout for JUMPI is [cond, dest] (dest on top). Push cond first so the
        // PUSH2 immediately precedes JUMPI - the fusion trigger.
        //   [0] PUSH1 0x01       (condition = truthy)
        //   [2] PUSH2 0x0009
        //   [5] JUMPI
        //   [6] PUSH1 0xFF       (fall-through, should NOT execute)
        //   [8] STOP
        //   [9] JUMPDEST
        //   [10] PUSH1 0x42
        //   [12] PUSH1 0x00
        //   [14] SSTORE
        //   [15] STOP
        byte[] dest = [0x00, 0x09];
        byte[] code = Prepare.EvmCode
            .PushData((byte)0x01)
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
        AssertGas(r, 41023);
    }

    [Test]
    public void PUSH2_JUMPI_not_taken_falls_through()
    {
        // Condition = 0: the fused PUSH2+JUMPI path must skip over the 2-byte immediate
        // and the JUMPI opcode, resuming at the instruction that follows.
        //   [0] PUSH1 0x00       (condition = false)
        //   [2] PUSH2 0x000C
        //   [5] JUMPI
        //   [6] PUSH1 0x11       (fall-through, MUST execute)
        //   [8] PUSH1 0x00
        //   [10] SSTORE
        //   [11] STOP
        //   [12] JUMPDEST
        //   [13] PUSH1 0x22      (MUST NOT execute)
        //   [15] PUSH1 0x00
        //   [17] SSTORE
        //   [18] STOP
        byte[] dest = [0x00, 0x0C];
        byte[] code = Prepare.EvmCode
            .PushData((byte)0x00)
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
        // Not-taken JUMPI: JUMPDEST is never entered, so double-charge wouldn't fire here.
        // Gas still pinned to catch unrelated regressions.
        AssertGas(r, 41022);
    }

    [Test]
    public void PUSH2_JUMP_to_invalid_destination_reverts_and_does_not_write()
    {
        // 0x0005 is not a JUMPDEST; PUSH2+JUMP fusion must surface
        // InvalidJumpDestination and prevent any post-jump effects.
        //   [0] PUSH2 0x0005
        //   [3] JUMP
        //   [4] STOP
        //   [5] PUSH1 0x99       (would run if the invalid jump somehow landed)
        //   [7] PUSH1 0x00
        //   [9] SSTORE
        //   [10] STOP
        byte[] dest = [0x00, 0x05];
        byte[] code = Prepare.EvmCode
            .PushData(dest)
            .Op(Instruction.JUMP)
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
}
