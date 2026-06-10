// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.CodeAnalysis.IlEvm;

public enum OpKind
{
    Linear,
    Jump,
    ConditionalJump,
    Terminator,
    JumpDest,
}

public readonly record struct OpInfo(long StaticGas, int Pops, int Pushes, int ImmediateBytes, OpKind Kind, bool HasDynamicGas = false);

/// <summary>
/// Metadata for the IL-EVM v1 opcode subset: fork-invariant compute, stack and memory opcodes
/// whose gas is static (memory opcodes additionally charge their dynamic expansion cost at the
/// opcode, mirroring the interpreter). Gas values come from the same <see cref="GasCostOf"/>
/// constants the interpreter handlers consume, so there is a single source of truth.
/// Anything not in this table is interpreter-only in v1.
/// </summary>
/// <remarks>
/// "v1" here is the ANALYZER's block-cut classification, a superset of what the segment
/// compiler can emit: MLOAD/MSTORE/MSTORE8 (and JUMP/JUMPI) are v1 so blocks aren't cut at
/// them, but <see cref="IlSegmentCompiler"/> stops its emittable prefix there and the
/// interpreter executes them. That is also why a "compilable" block's
/// <see cref="BasicBlock.StaticGas"/> must never be billed by a segment — the compiler
/// recomputes its own prefix gas in its PrefixMetrics and charges only that.
/// </remarks>
public static class IlEvmOpcodes
{
    /// <summary>
    /// Returns metadata when <paramref name="instruction"/> belongs to the v1 subset under the
    /// given spec. Spec-gated members (SHL/SHR/SAR, PUSH0) are excluded on forks that predate
    /// them — there they decode as invalid instructions, which are interpreter territory.
    /// </summary>
    public static bool TryGetV1(Instruction instruction, IReleaseSpec spec, out OpInfo info)
    {
        switch (instruction)
        {
            case Instruction.STOP:
                info = new OpInfo(StaticGas: 0, Pops: 0, Pushes: 0, ImmediateBytes: 0, OpKind.Terminator);
                return true;

            case Instruction.ADD:
            case Instruction.SUB:
                info = new OpInfo(GasCostOf.VeryLow, Pops: 2, Pushes: 1, ImmediateBytes: 0, OpKind.Linear);
                return true;
            case Instruction.MUL:
            case Instruction.DIV:
            case Instruction.SDIV:
            case Instruction.MOD:
            case Instruction.SMOD:
            case Instruction.SIGNEXTEND:
                info = new OpInfo(GasCostOf.Low, Pops: 2, Pushes: 1, ImmediateBytes: 0, OpKind.Linear);
                return true;
            case Instruction.ADDMOD:
            case Instruction.MULMOD:
                info = new OpInfo(GasCostOf.Mid, Pops: 3, Pushes: 1, ImmediateBytes: 0, OpKind.Linear);
                return true;

            case Instruction.LT:
            case Instruction.GT:
            case Instruction.SLT:
            case Instruction.SGT:
            case Instruction.EQ:
            case Instruction.AND:
            case Instruction.OR:
            case Instruction.XOR:
            case Instruction.BYTE:
                info = new OpInfo(GasCostOf.VeryLow, Pops: 2, Pushes: 1, ImmediateBytes: 0, OpKind.Linear);
                return true;
            case Instruction.ISZERO:
            case Instruction.NOT:
                info = new OpInfo(GasCostOf.VeryLow, Pops: 1, Pushes: 1, ImmediateBytes: 0, OpKind.Linear);
                return true;

            case Instruction.SHL:
            case Instruction.SHR:
            case Instruction.SAR:
                if (!spec.ShiftOpcodesEnabled) break;
                info = new OpInfo(GasCostOf.VeryLow, Pops: 2, Pushes: 1, ImmediateBytes: 0, OpKind.Linear);
                return true;

            case Instruction.POP:
                info = new OpInfo(GasCostOf.Base, Pops: 1, Pushes: 0, ImmediateBytes: 0, OpKind.Linear);
                return true;

            case Instruction.MLOAD:
            case Instruction.CALLDATALOAD:
                info = new OpInfo(GasCostOf.VeryLow, Pops: 1, Pushes: 1, ImmediateBytes: 0, OpKind.Linear, HasDynamicGas: instruction == Instruction.MLOAD);
                return true;
            case Instruction.MSTORE:
            case Instruction.MSTORE8:
                info = new OpInfo(GasCostOf.VeryLow, Pops: 2, Pushes: 0, ImmediateBytes: 0, OpKind.Linear, HasDynamicGas: true);
                return true;
            case Instruction.KECCAK256:
                info = new OpInfo(GasCostOf.Sha3, Pops: 2, Pushes: 1, ImmediateBytes: 0, OpKind.Linear, HasDynamicGas: true);
                return true;

            // Storage and environment opcodes execute as embedded handler calls; their gas is
            // charged inside the handlers (spec/state-dependent for storage).
            case Instruction.SLOAD:
                info = new OpInfo(StaticGas: 0, Pops: 1, Pushes: 1, ImmediateBytes: 0, OpKind.Linear, HasDynamicGas: true);
                return true;
            case Instruction.SSTORE:
                info = new OpInfo(StaticGas: 0, Pops: 2, Pushes: 0, ImmediateBytes: 0, OpKind.Linear, HasDynamicGas: true);
                return true;
            case Instruction.GAS:
            case Instruction.CALLER:
            case Instruction.ADDRESS:
            case Instruction.CALLVALUE:
            case Instruction.CALLDATASIZE:
                info = new OpInfo(GasCostOf.Base, Pops: 0, Pushes: 1, ImmediateBytes: 0, OpKind.Linear);
                return true;

            case Instruction.JUMP:
                info = new OpInfo(GasCostOf.Mid, Pops: 1, Pushes: 0, ImmediateBytes: 0, OpKind.Jump);
                return true;
            case Instruction.JUMPI:
                info = new OpInfo(GasCostOf.High, Pops: 2, Pushes: 0, ImmediateBytes: 0, OpKind.ConditionalJump);
                return true;
            case Instruction.JUMPDEST:
                info = new OpInfo(GasCostOf.JumpDest, Pops: 0, Pushes: 0, ImmediateBytes: 0, OpKind.JumpDest);
                return true;

            case Instruction.PUSH0:
                if (!spec.IncludePush0Instruction) break;
                info = new OpInfo(GasCostOf.Base, Pops: 0, Pushes: 1, ImmediateBytes: 0, OpKind.Linear);
                return true;

            case >= Instruction.PUSH1 and <= Instruction.PUSH32:
                info = new OpInfo(GasCostOf.VeryLow, Pops: 0, Pushes: 1, ImmediateBytes: instruction - Instruction.PUSH1 + 1, OpKind.Linear);
                return true;

            case >= Instruction.DUP1 and <= Instruction.DUP16:
                {
                    int depth = instruction - Instruction.DUP1 + 1;
                    info = new OpInfo(GasCostOf.VeryLow, Pops: depth, Pushes: depth + 1, ImmediateBytes: 0, OpKind.Linear);
                    return true;
                }

            case >= Instruction.SWAP1 and <= Instruction.SWAP16:
                {
                    int depth = instruction - Instruction.SWAP1 + 2;
                    info = new OpInfo(GasCostOf.VeryLow, Pops: depth, Pushes: depth, ImmediateBytes: 0, OpKind.Linear);
                    return true;
                }
        }

        info = default;
        return false;
    }

    /// <summary>
    /// Opcodes that end linear control flow even though they are outside the v1 subset — the
    /// analyzer must cut blocks after them and treat the following position as a leader.
    /// Undefined opcodes also fault, but their blocks are interpreter-only anyway, so block
    /// boundaries around them carry no compilation consequences.
    /// </summary>
    public static bool IsNonV1Terminator(Instruction instruction) =>
        instruction is Instruction.RETURN or Instruction.REVERT or Instruction.INVALID or Instruction.SELFDESTRUCT;
}
