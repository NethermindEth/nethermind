// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.CodeAnalysis;

/// <summary>
/// Values are ordered so that: "carries the block charge" is <c>Kind &lt;= FusedBlockFirst</c>,
/// "is precharged" is <c>Kind &lt; Boundary</c>, "is a fused pair" is <c>(Kind &amp; 1) == 1</c>.
/// </summary>
internal enum StreamOpKind : byte
{
    BlockFirst = 0,
    FusedBlockFirst = 1,
    InBlock = 2,
    FusedInBlock = 3,
    StaticJump = 4,
    StaticJumpI = 5,
    Boundary = 6,
}

/// <summary>
/// Virtual opcodes for fused PUSH+op pairs, placed in byte values the EVM does not define
/// (0x0C..0x0F and 0x21..0x2F gaps). The fingerprint gate keeps new forks (which might define
/// one of these) off the stream until reviewed.
/// </summary>
internal static class FusedOpcode
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

    /// <summary>Binary ops a preceding in-block PUSH folds into; must match the executor's fused cases exactly.</summary>
    public static bool TryMap(Instruction instruction, out byte fused)
    {
        fused = instruction switch
        {
            Instruction.ADD => Add,
            Instruction.SUB => Sub,
            Instruction.MUL => Mul,
            Instruction.DIV => Div,
            Instruction.SDIV => SDiv,
            Instruction.MOD => Mod,
            Instruction.SMOD => SMod,
            Instruction.LT => Lt,
            Instruction.GT => Gt,
            Instruction.SLT => SLt,
            Instruction.SGT => SGt,
            Instruction.EQ => Eq,
            Instruction.AND => And,
            Instruction.OR => Or,
            Instruction.XOR => Xor,
            Instruction.SHL => Shl,
            Instruction.SHR => Shr,
            _ => 0,
        };
        return fused != 0;
    }
}

/// <summary>
/// One pre-decoded instruction (or fused PUSH+op pair). Hot-first layout: dispatch fields fit
/// the first 8 bytes; <see cref="Operand"/> is loaded only by the cases that need it.
/// </summary>
internal readonly struct StreamOp(byte opcode, StreamOpKind kind, ushort pc, ushort blockIndex, byte advance, ulong operand)
{
    public readonly byte Opcode = opcode;
    public readonly StreamOpKind Kind = kind;
    /// <summary>Code bytes this entry covers (opcode + immediates; both for a fused pair).</summary>
    public readonly byte Advance = advance;
    public readonly ushort BlockIndex = blockIndex;
    public readonly ushort Pc = pc;
    /// <summary>In-block PUSH immediate (value for widths ≤8 bytes, else index into
    /// <see cref="InstructionStream.Constants"/>); for a fused pair, the constant the op consumes.</summary>
    public readonly ulong Operand = operand;
}

/// <summary>
/// Bytecode preprocessed into a flat instruction stream with per-basic-block static gas sums
/// and fused PUSH+op superinstructions, built once per <see cref="CodeInfo"/> and shared by
/// every execution of that code.
/// </summary>
/// <remarks>
/// Consensus invariants: only static-gas ops are precharged. The actual gate is
/// <c>spec.IncludePush0Instruction</c> — i.e. ANY fork &gt;= Shanghai runs the stream; there is no
/// upper-bound fork check. The precharged gas costs are assumed fork-stable and MUST be revalidated
/// whenever a new fork changes any of them. A JUMPDEST is a solo block; a truncated trailing PUSH stays
/// a boundary op; nothing lands inside a fused pair; the executor recomputes the entry from the landing
/// pc and re-meters any block entered past its charging entry (metered dispatch reads raw code, so gas
/// stays exact).
/// </remarks>
internal sealed class InstructionStream
{
    public const ushort InvalidEntry = ushort.MaxValue;

    public readonly StreamOp[] Ops;
    public readonly ulong[] BlockGas;
    /// <summary>Pool for pre-decoded PUSH9..PUSH32 constants, referenced by entry operand.</summary>
    public readonly UInt256[] Constants;
    /// <summary>The same pool in stack representation (32 big-endian bytes per constant), so
    /// fused bitwise ops run as straight vector loads with no limb conversion.</summary>
    public readonly byte[] ConstantBytes;
    /// <summary>Entry index for every entry-start pc; <see cref="InvalidEntry"/> for immediate
    /// bytes and fused-pair interiors; index one past the last op at pc == code length.</summary>
    public readonly ushort[] PcToEntry;
    private InstructionStream(StreamOp[] ops, ulong[] blockGas, UInt256[] constants, ushort[] pcToEntry, bool buildConstantBytes)
    {
        Ops = ops;
        BlockGas = blockGas;
        Constants = constants;
        PcToEntry = pcToEntry;

        // Only the fused bitwise cores index ConstantBytes; arithmetic/shift fusion reads the UInt256
        // Constants form. Skip the big-endian copy entirely when no bitwise fusion was emitted.
        if (buildConstantBytes)
        {
            ConstantBytes = new byte[constants.Length * 32];
            for (int i = 0; i < constants.Length; i++)
            {
                constants[i].ToBigEndian(ConstantBytes.AsSpan(i * 32, 32));
            }
        }
        else
        {
            ConstantBytes = [];
        }
    }

    public static InstructionStream? TryBuild(ReadOnlySpan<byte> code)
    {
        if (code.Length == 0 || code.Length >= ushort.MaxValue)
            return null;

        List<StreamOp> ops = new(code.Length / 2);
        List<ulong> blockGas = new(code.Length / 16);
        List<UInt256> constants = new(code.Length / 32);
        ushort[] pcToEntry = new ushort[code.Length + 1];
        pcToEntry.AsSpan().Fill(InvalidEntry);

        int openBlock = -1;
        int pc = 0;
        // ConstantBytes (the big-endian form) is read only by the fused bitwise cores; track whether any
        // get emitted so a stream whose constants feed only arithmetic/shift fusion skips that allocation.
        bool anyBitwiseFusion = false;
        while (pc < code.Length)
        {
            Instruction instruction = (Instruction)code[pc];
            int immediates = GetImmediateByteCount(instruction);
            int size = 1 + immediates;
            pcToEntry[pc] = (ushort)ops.Count;

            if (instruction == Instruction.JUMPDEST)
            {
                // Solo block: a fused PUSH2+JUMP lands one past the JUMPDEST having self-charged it,
                // so the following ops must sit in their own separately charged block.
                blockGas.Add(GasCostOf.JumpDest);
                ops.Add(new StreamOp((byte)instruction, StreamOpKind.BlockFirst, (ushort)pc, (ushort)(blockGas.Count - 1), 1, 0));
                openBlock = -1;
            }
            else if (GetInBlockCost(instruction) is ulong cost && cost != NotInBlock && pc + immediates < code.Length)
            {
                if (openBlock >= 0
                    && FusedOpcode.TryMap(instruction, out byte fusedOpcode)
                    && TryTakePrecedingPush(ops, out StreamOp push))
                {
                    // Pair becomes one entry: constant goes to the pool (one indexed load, no
                    // per-width branching) and the pc map forgets this start (nothing lands in a pair).
                    anyBitwiseFusion |= fusedOpcode is >= FusedOpcode.Eq and <= FusedOpcode.Xor;
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
                // PUSH2 const + JUMP/JUMPI to a validated JUMPDEST: one entry, target resolved to an
                // entry index by the fixup pass below. Push+jump gas is self-charged at execution; the
                // landing JUMPDEST's solo block charges itself like a taken dynamic jump would.
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
                // Dynamic JUMP/JUMPI/PUSH2 and trailing truncated PUSHes.
                openBlock = -1;
                ops.Add(new StreamOp((byte)instruction, StreamOpKind.Boundary, (ushort)pc, 0, (byte)size, 0));
            }

            pc += size;
        }

        // Entry indexes are ushort; oversized streams fall back to the bytecode loop.
        if (ops.Count >= InvalidEntry)
            return null;

        pcToEntry[code.Length] = (ushort)ops.Count;

        // Resolve static jump target pcs to entry indexes now that every entry exists.
        for (int i = 0; i < ops.Count; i++)
        {
            StreamOp op = ops[i];
            if (op.Kind is StreamOpKind.StaticJump or StreamOpKind.StaticJumpI)
            {
                ushort targetEntry = pcToEntry[(int)op.Operand];
                // InvalidEntry means the 0x5B target is a PUSH immediate, not a real JUMPDEST. Refuse
                // to stream so the bytecode loop's ValidateJump produces the correct failure.
                if (targetEntry == InvalidEntry)
                    return null;
                ops[i] = new StreamOp(op.Opcode, op.Kind, op.Pc, op.BlockIndex, op.Advance, targetEntry);
            }
        }

        return new InstructionStream(ops.ToArray(), blockGas.ToArray(), constants.ToArray(), pcToEntry, anyBitwiseFusion);
    }

    /// <summary>
    /// The static-cost op set run unmetered; must match the executor's in-block switch exactly.
    /// PUSH2 excluded (keeps fused PUSH2+JUMP); PUSH1 and PUSH3..PUSH32 are included. DUP9+/SWAP9+
    /// excluded to keep the switch within the size the JIT inlines.
    /// </summary>
    public const ulong NotInBlock = ulong.MaxValue;

    public static ulong GetInBlockCost(Instruction instruction) => instruction switch
    {
        Instruction.ADD or Instruction.SUB or Instruction.LT or Instruction.GT or Instruction.SLT
            or Instruction.SGT or Instruction.EQ or Instruction.AND or Instruction.OR or Instruction.XOR
            or Instruction.ISZERO or Instruction.NOT or Instruction.SHL or Instruction.SHR
            or Instruction.PUSH1
            or (>= Instruction.PUSH3 and <= Instruction.PUSH32)
            or (>= Instruction.DUP1 and <= Instruction.DUP8)
            or (>= Instruction.SWAP1 and <= Instruction.SWAP8) => GasCostOf.VeryLow,
        Instruction.MUL or Instruction.DIV or Instruction.SDIV or Instruction.MOD or Instruction.SMOD => GasCostOf.Low,
        Instruction.POP or Instruction.PUSH0 => GasCostOf.Base,
        _ => NotInBlock,
    };

    /// <summary>Returns the PUSH2 immediate at <paramref name="pc"/> when it points at a JUMPDEST; -1 otherwise.</summary>
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
