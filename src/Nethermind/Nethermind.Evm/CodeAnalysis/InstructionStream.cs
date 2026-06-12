// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.CodeAnalysis;

/// <summary>
/// How the stream executor dispatches a <see cref="StreamOp"/>. Order and parity matter:
/// "carries the block charge" is <c>Kind &lt;= FusedBlockFirst</c>, "is precharged" is
/// <c>Kind &lt; Boundary</c>, and "is a fused PUSH+op pair" is <c>(Kind &amp; 1) == 1</c>.
/// </summary>
public enum StreamOpKind : byte
{
    /// <summary>First op of a basic block: charges the block's summed static gas.</summary>
    BlockFirst = 0,
    /// <summary>Fused PUSH+op pair that also opens its block.</summary>
    FusedBlockFirst = 1,
    /// <summary>Static-cost op inside a precharged block; runs the gas-free core.</summary>
    InBlock = 2,
    /// <summary>Fused PUSH+op pair inside a precharged block: the op runs against the
    /// pre-decoded push operand directly on the stack top.</summary>
    FusedInBlock = 3,
    /// <summary>PUSH2 const + JUMP with an analysis-validated JUMPDEST: jumps straight to the
    /// pre-resolved target entry, self-charging its static gas.</summary>
    StaticJump = 4,
    /// <summary>PUSH2 const + JUMPI with an analysis-validated JUMPDEST: pops the condition
    /// and jumps to the pre-resolved target entry or falls through.</summary>
    StaticJumpI = 5,
    /// <summary>Any other op: standard table handler with the full per-op epilogue.</summary>
    Boundary = 6,
}

/// <summary>
/// Virtual opcodes for fused PUSH+op pairs, placed in byte values the EVM does not define
/// (0x0C..0x0F and 0x21..0x2F gaps). Only the analyzer creates entries with these values; a
/// future fork defining one of them would surface as a boundary op (the in-block set is
/// explicit), and the fingerprint gate keeps new forks off the stream until reviewed.
/// </summary>
public static class FusedOpcode
{
    public const byte Add = 0x0C;
    public const byte Sub = 0x0D;
    public const byte Mul = 0x0E;
    public const byte Div = 0x0F;
    public const byte SDiv = 0x21;
    public const byte Mod = 0x22;
    public const byte SMod = 0x23;
    public const byte Lt = 0x24;
    public const byte Gt = 0x25;
    public const byte SLt = 0x26;
    public const byte SGt = 0x27;
    public const byte Eq = 0x28;
    public const byte And = 0x29;
    public const byte Or = 0x2A;
    public const byte Xor = 0x2B;
    public const byte Shl = 0x2C;
    public const byte Shr = 0x2D;
    public const byte StaticJump = 0x2E;
    public const byte StaticJumpI = 0x2F;

    /// <summary>
    /// Binary ops whose first operand is the stack top — a preceding in-block PUSH constant
    /// folds into them. Must match the executor's fused cases exactly.
    /// </summary>
    public static bool TryMap(Instruction instruction, out byte fused)
    {
        switch (instruction)
        {
            case Instruction.ADD: fused = Add; return true;
            case Instruction.SUB: fused = Sub; return true;
            case Instruction.MUL: fused = Mul; return true;
            case Instruction.DIV: fused = Div; return true;
            case Instruction.SDIV: fused = SDiv; return true;
            case Instruction.MOD: fused = Mod; return true;
            case Instruction.SMOD: fused = SMod; return true;
            case Instruction.LT: fused = Lt; return true;
            case Instruction.GT: fused = Gt; return true;
            case Instruction.SLT: fused = SLt; return true;
            case Instruction.SGT: fused = SGt; return true;
            case Instruction.EQ: fused = Eq; return true;
            case Instruction.AND: fused = And; return true;
            case Instruction.OR: fused = Or; return true;
            case Instruction.XOR: fused = Xor; return true;
            case Instruction.SHL: fused = Shl; return true;
            case Instruction.SHR: fused = Shr; return true;
            default: fused = 0; return false;
        }
    }
}

/// <summary>
/// One pre-decoded instruction (or fused PUSH+op pair) of an <see cref="InstructionStream"/>.
/// </summary>
public readonly struct StreamOp(byte opcode, StreamOpKind kind, ushort pc, ushort blockIndex, byte advance, ulong operand)
{
    // Hot-first layout: the dispatch fields read every iteration live in the first 8 bytes
    // (one 64-bit load); Operand is loaded lazily by the cases that need it.
    /// <summary>For a fused pair: the second op's opcode (the push survives as <see cref="Operand"/>).</summary>
    public readonly byte Opcode = opcode;
    public readonly StreamOpKind Kind = kind;
    /// <summary>Total code bytes this entry covers (opcode + immediates; both for a fused pair).</summary>
    public readonly byte Advance = advance;
    /// <summary>For block-charging kinds: index into <see cref="InstructionStream.BlockGas"/>.</summary>
    public readonly ushort BlockIndex = blockIndex;
    public readonly ushort Pc = pc;
    /// <summary>Pre-decoded immediate of an in-block PUSH: the value itself for widths up to
    /// 8 bytes, or an index into <see cref="InstructionStream.Constants"/> for wider pushes
    /// (the push width is derivable from <see cref="Advance"/>). For a fused pair this is the
    /// pushed constant the op consumes.</summary>
    public readonly ulong Operand = operand;
}

/// <summary>
/// Bytecode preprocessed into a flat instruction stream with per-basic-block static gas sums
/// and fused PUSH+op superinstructions, built once per <see cref="CodeInfo"/> and shared by
/// every execution of that code.
/// </summary>
/// <remarks>
/// Consensus invariants the analyzer maintains:
/// <list type="bullet">
/// <item>Only ops whose gas is a spec-independent constant at Shanghai+ are precharged;
/// callers must gate stream execution on a tip-fork dispatch fingerprint.</item>
/// <item>A JUMPDEST is always a solo block, so a fused PUSH2+JUMP landing one past it never
/// skips an uncharged block prefix.</item>
/// <item>PUSH immediates are pre-decoded only when fully present in code; a truncated
/// trailing PUSH stays a boundary op so the table handler keeps its exact padding semantics.</item>
/// <item>No landing pc can point inside a fused pair: jumps land on JUMPDESTs, table-fused
/// handlers land on the instruction after the ones they consumed, and resumes land after a
/// CALL-family boundary — all entry starts.</item>
/// <item>The executor recomputes the entry index from the landing pc after every table call
/// (fused table handlers consume multiple instructions) and re-meters any block entered past
/// its charging entry; metered dispatch reads raw code, so it is exact regardless of merges.</item>
/// </list>
/// </remarks>
public sealed class InstructionStream
{
    /// <summary>Entry-index sentinel for program counters that are not an entry start.</summary>
    public const ushort InvalidEntry = ushort.MaxValue;

    public readonly StreamOp[] Ops;
    public readonly long[] BlockGas;
    /// <summary>Pool for pre-decoded PUSH9..PUSH32 constants, referenced by entry operand.</summary>
    public readonly UInt256[] Constants;
    /// <summary>The same pool in stack representation (32 big-endian bytes per constant), so
    /// fused bitwise ops run as straight vector loads with no limb conversion.</summary>
    public readonly byte[] ConstantBytes;
    /// <summary>Entry index for every entry-start pc; <see cref="InvalidEntry"/> for immediate
    /// bytes and fused-pair interiors; index one past the last op at pc == code length.</summary>
    public readonly ushort[] PcToEntry;
    private InstructionStream(StreamOp[] ops, long[] blockGas, UInt256[] constants, ushort[] pcToEntry)
    {
        Ops = ops;
        BlockGas = blockGas;
        Constants = constants;
        PcToEntry = pcToEntry;

        ConstantBytes = new byte[constants.Length * 32];
        for (int i = 0; i < constants.Length; i++)
        {
            constants[i].ToBigEndian(ConstantBytes.AsSpan(i * 32, 32));
        }
    }

    public static InstructionStream? TryBuild(ReadOnlySpan<byte> code)
    {
        if (code.Length == 0 || code.Length >= ushort.MaxValue)
            return null;

        List<StreamOp> ops = new(code.Length / 2);
        List<long> blockGas = new(code.Length / 16);
        List<UInt256> constants = new(code.Length / 32);
        ushort[] pcToEntry = new ushort[code.Length + 1];
        pcToEntry.AsSpan().Fill(InvalidEntry);

        int openBlock = -1;
        int pc = 0;
        while (pc < code.Length)
        {
            Instruction instruction = (Instruction)code[pc];
            int immediates = GetImmediateByteCount(instruction);
            int size = 1 + immediates;
            pcToEntry[pc] = (ushort)ops.Count;

            if (instruction == Instruction.JUMPDEST)
            {
                // Solo block: a fused PUSH2+JUMP lands one past the JUMPDEST having charged it
                // itself, so the ops that follow must sit in their own, separately charged block.
                blockGas.Add(GasCostOf.JumpDest);
                ops.Add(new StreamOp((byte)instruction, StreamOpKind.BlockFirst, (ushort)pc, (ushort)(blockGas.Count - 1), 1, 0));
                openBlock = -1;
            }
            else if (TryGetInBlockCost(instruction, out long cost) && pc + immediates < code.Length)
            {
                if (openBlock >= 0
                    && FusedOpcode.TryMap(instruction, out byte fusedOpcode)
                    && TryTakePrecedingPush(ops, out StreamOp push))
                {
                    // The pair becomes one entry under a virtual opcode: the pushed constant
                    // always lives in the pool (one indexed load at execution, no per-width
                    // branching), and the pc map forgets this op's own start (nothing can
                    // land inside a pair).
                    blockGas[openBlock] += cost;
                    pcToEntry[pc] = InvalidEntry;
                    ulong poolIndex;
                    if ((Instruction)push.Opcode is >= Instruction.PUSH9 and <= Instruction.PUSH32)
                    {
                        poolIndex = push.Operand;
                    }
                    else
                    {
                        constants.Add(push.Operand);
                        poolIndex = (ulong)(constants.Count - 1);
                    }

                    StreamOpKind fusedKind = push.Kind == StreamOpKind.BlockFirst
                        ? StreamOpKind.FusedBlockFirst
                        : StreamOpKind.FusedInBlock;
                    ops[^1] = new StreamOp(fusedOpcode, fusedKind, push.Pc, push.BlockIndex, (byte)(push.Advance + size), poolIndex);
                }
                else
                {
                    ulong operand = 0;
                    if (instruction is >= Instruction.PUSH1 and <= Instruction.PUSH8)
                    {
                        operand = ReadImmediate(code.Slice(pc + 1, immediates));
                    }
                    else if (instruction is >= Instruction.PUSH9 and <= Instruction.PUSH32)
                    {
                        constants.Add(ReadWideImmediate(code.Slice(pc + 1, immediates)));
                        operand = (ulong)(constants.Count - 1);
                    }

                    StreamOpKind kind = StreamOpKind.InBlock;
                    if (openBlock < 0)
                    {
                        blockGas.Add(0);
                        openBlock = blockGas.Count - 1;
                        kind = StreamOpKind.BlockFirst;
                    }

                    blockGas[openBlock] += cost;
                    ops.Add(new StreamOp((byte)instruction, kind, (ushort)pc, (ushort)openBlock, (byte)size, operand));
                }
            }
            else if (instruction == Instruction.PUSH2
                && pc + 3 < code.Length
                && (Instruction)code[pc + 3] is Instruction.JUMP or Instruction.JUMPI
                && TryReadStaticJumpTarget(code, pc) is int dest and >= 0)
            {
                // PUSH2 const + JUMP/JUMPI with an analysis-validated JUMPDEST: one entry,
                // jump target resolved to an entry index by the fixup pass below. Gas
                // (push + jump) is self-charged at execution; the landing JUMPDEST's solo
                // block charges itself exactly as a taken dynamic jump would.
                bool conditional = (Instruction)code[pc + 3] == Instruction.JUMPI;
                openBlock = -1;
                ops.Add(new StreamOp(
                    conditional ? FusedOpcode.StaticJumpI : FusedOpcode.StaticJump,
                    conditional ? StreamOpKind.StaticJumpI : StreamOpKind.StaticJump,
                    (ushort)pc, 0, 4, (ulong)dest));
                pc += 4;
                continue;
            }
            else
            {
                // Includes dynamic JUMP/JUMPI/PUSH2 (the table keeps the fused PUSH2+JUMP
                // handler) and a trailing PUSH whose immediates are truncated by the end of
                // code.
                openBlock = -1;
                ops.Add(new StreamOp((byte)instruction, StreamOpKind.Boundary, (ushort)pc, 0, (byte)size, 0));
            }

            pc += size;
        }

        // Entry indexes live in the ushort pc map; oversized streams fall back to the
        // bytecode loop.
        if (ops.Count >= InvalidEntry)
            return null;

        pcToEntry[code.Length] = (ushort)ops.Count;

        // Static jump targets were recorded as destination pcs; resolve them to entry
        // indexes now that every entry exists (forward jumps included). A JUMPDEST is never
        // merged into a pair, so its pc always maps to a real entry.
        for (int i = 0; i < ops.Count; i++)
        {
            StreamOp op = ops[i];
            if (op.Kind is StreamOpKind.StaticJump or StreamOpKind.StaticJumpI)
            {
                ops[i] = new StreamOp(op.Opcode, op.Kind, op.Pc, op.BlockIndex, op.Advance, pcToEntry[(int)op.Operand]);
            }
        }

        return new InstructionStream(ops.ToArray(), blockGas.ToArray(), constants.ToArray(), pcToEntry);
    }

    /// <summary>
    /// The static-cost op set the stream executor runs unmetered; must match the executor's
    /// in-block switch exactly. PUSH2 is excluded (keeps the table's fused PUSH2+JUMP);
    /// PUSH9+ and DUP9+/SWAP9+ are excluded to keep the executor switch within the size the
    /// JIT inlines.
    /// </summary>
    public static bool TryGetInBlockCost(Instruction instruction, out long cost)
    {
        switch (instruction)
        {
            case Instruction.ADD:
            case Instruction.SUB:
            case Instruction.LT:
            case Instruction.GT:
            case Instruction.SLT:
            case Instruction.SGT:
            case Instruction.EQ:
            case Instruction.AND:
            case Instruction.OR:
            case Instruction.XOR:
            case Instruction.ISZERO:
            case Instruction.NOT:
            case Instruction.SHL:
            case Instruction.SHR:
            case Instruction.PUSH1:
            case >= Instruction.PUSH3 and <= Instruction.PUSH32:
            case >= Instruction.DUP1 and <= Instruction.DUP8:
            case >= Instruction.SWAP1 and <= Instruction.SWAP8:
                cost = GasCostOf.VeryLow;
                return true;
            case Instruction.MUL:
            case Instruction.DIV:
            case Instruction.SDIV:
            case Instruction.MOD:
            case Instruction.SMOD:
                cost = GasCostOf.Low;
                return true;
            case Instruction.POP:
            case Instruction.PUSH0:
                cost = GasCostOf.Base;
                return true;
            default:
                cost = 0;
                return false;
        }
    }

    /// <summary>Reads the PUSH2 immediate at <paramref name="pc"/> and returns it when it
    /// points at a JUMPDEST; -1 otherwise (the pair then stays a boundary op and fails at
    /// runtime exactly like a dynamic jump).</summary>
    private static int TryReadStaticJumpTarget(ReadOnlySpan<byte> code, int pc)
    {
        int dest = (code[pc + 1] << 8) | code[pc + 2];
        return dest < code.Length && (Instruction)code[dest] == Instruction.JUMPDEST ? dest : -1;
    }

    private static bool TryTakePrecedingPush(List<StreamOp> ops, out StreamOp push)
    {
        push = default;
        if (ops.Count == 0)
            return false;

        StreamOp last = ops[^1];
        if (last.Kind is not (StreamOpKind.BlockFirst or StreamOpKind.InBlock))
            return false;
        if ((Instruction)last.Opcode is not (Instruction.PUSH1 or >= Instruction.PUSH3 and <= Instruction.PUSH32))
            return false;

        push = last;
        return true;
    }

    private static ulong ReadImmediate(ReadOnlySpan<byte> immediates)
    {
        ulong result = 0;
        for (int i = 0; i < immediates.Length; i++)
        {
            result = (result << 8) | immediates[i];
        }

        return result;
    }

    [System.Runtime.CompilerServices.SkipLocalsInit]
    private static UInt256 ReadWideImmediate(ReadOnlySpan<byte> immediates)
    {
        Span<byte> padded = stackalloc byte[32];
        padded.Clear();
        immediates.CopyTo(padded.Slice(32 - immediates.Length));
        return new UInt256(padded, isBigEndian: true);
    }

    private static int GetImmediateByteCount(Instruction instruction)
        => instruction is >= Instruction.PUSH1 and <= Instruction.PUSH32
            ? instruction - Instruction.PUSH1 + 1
            : 0;
}
