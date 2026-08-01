// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
    /// <summary>Self-charging table op that neither redirects control nor ends the frame, so the
    /// open block continues across it: the executor advances sequentially with no landing
    /// recompute, and the ops after it keep their precharge in the same block.</summary>
    BoundaryLinear = 7,
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
    // Glue pairs. 0x1E is CLZ and 0x4B is SLOTNUM in this tree, so the only bytes left undefined
    // below the 0xA5 range are 0x1F and 0x4C..0x4F; the 0xA5.. range is avoided because the
    // frame-transaction work claims part of it. Verified against Instruction.cs, not assumed.
    public const byte Push1Push1 = 0x1F;
    public const byte StaticJumpINot = 0x4F;
    public const byte AndIsZero = 0x4C;
    public const byte PopPop = 0x4D;
    public const byte SwapPop = 0x4E;
    // 0xA5..0xB4 is left alone: the frame-transaction work claims part of that range.
    public const byte Push1Dup = 0xC0;
    public const byte SubAnd = 0xC1;
    public const byte ShlSub = 0xC2;

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
/// stays exact). An elided JUMPDEST is the pc map's only forward mapping, and a landing that finds one
/// (entry pc past the landing pc) charges the marker itself — the block charge that carries it was
/// bypassed by that arrival.
/// </remarks>
internal sealed class InstructionStream
{
    public const ushort InvalidEntry = ushort.MaxValue;

    public readonly StreamOp[] Ops;
    public readonly ulong[] BlockGas;
    /// <summary>Ops the bytecode loop would execute per block (fused pairs count as two, an elided
    /// JUMPDEST counts in the block that carries its gas). Consumed once per block charge, so the
    /// hot loop drops its per-op counter updates; a block that faults mid-run is counted whole.</summary>
    public readonly ushort[] BlockOpCount;
    /// <summary>Pool for pre-decoded PUSH9..PUSH32 constants, referenced by entry operand.</summary>
    public readonly UInt256[] Constants;
    /// <summary>The same pool in stack representation (32 big-endian bytes per constant), so
    /// fused bitwise ops run as straight vector loads with no limb conversion.</summary>
    public readonly byte[] ConstantBytes;
    /// <summary>Entry index for every entry-start pc; <see cref="InvalidEntry"/> for immediate
    /// bytes and fused-pair interiors; index one past the last op at pc == code length.</summary>
    public readonly ushort[] PcToEntry;

    public int RetainedBytes =>
        Ops.Length * Unsafe.SizeOf<StreamOp>()
        + BlockGas.Length * sizeof(ulong)
        + BlockOpCount.Length * sizeof(ushort)
        + Constants.Length * Unsafe.SizeOf<UInt256>()
        + ConstantBytes.Length
        + PcToEntry.Length * sizeof(ushort);

    private InstructionStream(StreamOp[] ops, ulong[] blockGas, ushort[] blockOpCount, UInt256[] constants, ushort[] pcToEntry, bool buildConstantBytes)
    {
        Ops = ops;
        BlockGas = blockGas;
        BlockOpCount = blockOpCount;
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
        List<ushort> blockOpCount = new(64);
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
                if (openBlock >= 0 && CanCarryJumpDestGas(code, pc + 1))
                {
                    // No entry: fall-through pays the marker as part of the block that flows into it
                    // (straight-line ops cannot divert), and both jump flavors charge it at the jump
                    // and land one entry past. The successor opens a fresh block below.
                    blockGas[openBlock] += GasCostOf.JumpDest;
                    blockOpCount[openBlock]++;
                    openBlock = -1;
                    pc += size;
                    continue;
                }

                // Solo block: a fused PUSH2+JUMP lands one past the JUMPDEST having self-charged it,
                // so the following ops must sit in their own separately charged block.
                blockGas.Add(GasCostOf.JumpDest);
                blockOpCount.Add(1);
                ops.Add(new StreamOp((byte)instruction, StreamOpKind.BlockFirst, (ushort)pc, (ushort)(blockGas.Count - 1), 1, 0));
                openBlock = -1;
            }
            else if (GetInBlockCost(instruction) is ulong cost && cost != NotInBlock && pc + immediates < code.Length)
            {
                if (openBlock >= 0
                    && TryFoldConstantPair(ops, constants, pcToEntry, instruction, pc, (byte)size))
                {
                    // Entry surgery happened inside; the original ops' gas and count stay in the block
                    // so the charge and the executed-op metric keep matching the bytecode loop.
                    blockGas[openBlock] += cost;
                    blockOpCount[openBlock]++;
                }
                else if (openBlock >= 0
                    && TryFuseGluePair(code, ops, instruction, pc, out StreamOp glued))
                {
                    blockGas[openBlock] += cost;
                    blockOpCount[openBlock]++;
                    pcToEntry[pc] = InvalidEntry;
                    ops[^1] = glued;
                }
                else if (openBlock >= 0
                    && FusedOpcode.TryMap(instruction, out byte fusedOpcode)
                    && TryTakePrecedingPush(ops, out StreamOp push)
                    // Folded entries can carry Advance near the byte ceiling; a wrapped sum would
                    // break the "advance spans the fused source bytes" invariant the landings rely on.
                    && push.Advance + size <= byte.MaxValue)
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
                    blockOpCount[openBlock]++;
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
                        blockOpCount.Add(0);
                        openBlock = blockGas.Count - 1;
                        kind = StreamOpKind.BlockFirst;
                    }

                    blockGas[openBlock] += cost;
                    blockOpCount[openBlock]++;
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

                // An ISZERO feeding a static JUMPI inverts into the jump: its gas is already in its
                // block's sum, so only the dispatch and the condition's stack round trip disappear.
                // Only a non-charging entry may fold away, and both interior pcs unmap so the
                // metered loop steps them raw.
                if (conditional
                    && ops.Count > 0
                    && ops[^1].Kind == StreamOpKind.InBlock
                    && (Instruction)ops[^1].Opcode == Instruction.ISZERO)
                {
                    StreamOp inverter = ops[^1];
                    ops.RemoveAt(ops.Count - 1);
                    pcToEntry[inverter.Pc] = InvalidEntry;
                    pcToEntry[pc] = InvalidEntry;
                    openBlock = -1;
                    ops.Add(new StreamOp(FusedOpcode.StaticJumpINot, StreamOpKind.StaticJumpI, inverter.Pc, 0, (byte)(inverter.Advance + 4), (ulong)dest));
                    pc += 4;
                    continue;
                }

                openBlock = -1;
                ops.Add(new StreamOp(
                    conditional ? FusedOpcode.StaticJumpI : FusedOpcode.StaticJump,
                    conditional ? StreamOpKind.StaticJumpI : StreamOpKind.StaticJump,
                    (ushort)pc, 0, 4, (ulong)dest));
                pc += 4;
                continue;
            }
            else if (IsLinearBoundary(instruction))
            {
                // The block stays open: the op self-charges and always falls through on success,
                // and any fault burns the frame's gas anyway, so the precharge above it is exact.
                ops.Add(new StreamOp((byte)instruction, StreamOpKind.BoundaryLinear, (ushort)pc, 0, (byte)size, 0));
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
                // The jump charges the JUMPDEST itself, so it lands one entry past a solo marker;
                // an elided marker has no entry and pcToEntry already points past it.
                if (targetEntry < ops.Count && (Instruction)ops[targetEntry].Opcode == Instruction.JUMPDEST)
                    targetEntry++;
                ops[i] = new StreamOp(op.Opcode, op.Kind, op.Pc, op.BlockIndex, op.Advance, targetEntry);
            }
        }

        return new InstructionStream(ops.ToArray(), blockGas.ToArray(), blockOpCount.ToArray(), constants.ToArray(), pcToEntry, anyBitwiseFusion);
    }

    /// <summary>
    /// The static-cost op set run unmetered; must match the executor's in-block switch exactly.
    /// PUSH2 excluded (keeps fused PUSH2+JUMP); PUSH1 and PUSH3..PUSH32 are included. DUP9+/SWAP9+
    /// excluded to keep the switch within the size the JIT inlines.
    /// </summary>
    public const ulong NotInBlock = ulong.MaxValue;

    /// <summary>Dynamic-gas ops that always fall through on success: no control redirect, no frame
    /// end, and no observation of remaining gas (which excludes GAS, the CALL family and CREATE:
    /// they either observe gas, forward it, or suspend the frame mid-block).</summary>
    public static bool IsLinearBoundary(Instruction instruction) => instruction switch
    {
        Instruction.MSTORE or Instruction.MLOAD or Instruction.MSTORE8 or Instruction.MCOPY
            or Instruction.KECCAK256 or Instruction.CALLDATALOAD or Instruction.CALLDATACOPY
            or Instruction.SLOAD => true,
        _ => false,
    };

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

    /// <summary>
    /// Folds PUSHx a; PUSHx b; binop into a single pooled push of the computed result. ADD through
    /// SGT run the executor's own operation implementations, so those cannot disagree on semantics
    /// (division by zero, wrapping); the bitwise ops and shifts below them are hand-mirrored from
    /// the executor's cores — the shift guard replicates ShiftCore's saturation test — so a change
    /// to either side must be made to both. Gas and the per-block op count are left to the caller:
    /// the block still charges and counts every original op, the stream just stops dispatching
    /// them. Cascades, because the folded entry is itself a const push - the (1 &lt;&lt; n) - 1
    /// mask idiom collapses to one entry from five.
    /// </summary>
    private static bool TryFoldConstantPair(List<StreamOp> ops, List<UInt256> constants, ushort[] pcToEntry, Instruction instruction, int pc, byte size)
    {
        if (ops.Count == 0)
            return false;

        // A glued PUSH1 pair holds both operands in one entry - the low byte was pushed first, so
        // the high byte is what an operator consumes first. Folding through it is what stops the
        // pair from leaving a runtime operation behind: on the measured workload this shape,
        // followed by a shift, is 2.35% of all dispatches.
        if (ops[^1].Opcode == FusedOpcode.Push1Push1
            && ops[^1].Kind is StreamOpKind.FusedInBlock or StreamOpKind.FusedBlockFirst)
        {
            StreamOp pair = ops[^1];
            int pairAdvance = pair.Advance + size;
            if (pairAdvance > byte.MaxValue)
                return false;

            UInt256 pairA = (pair.Operand >> 8) & 0xFF;
            UInt256 pairB = pair.Operand & 0xFF;
            if (!TryApplyConstOperation(instruction, in pairA, in pairB, out UInt256 pairResult))
                return false;

            constants.Add(pairResult);
            pcToEntry[pc] = InvalidEntry;
            ops[^1] = new StreamOp((byte)Instruction.PUSH32,
                pair.Kind == StreamOpKind.FusedBlockFirst ? StreamOpKind.BlockFirst : StreamOpKind.InBlock,
                pair.Pc, pair.BlockIndex, (byte)pairAdvance, (ulong)(constants.Count - 1));
            return true;
        }

        if (ops.Count < 2)
            return false;

        StreamOp top = ops[^1];
        StreamOp under = ops[^2];
        if (top.BlockIndex != under.BlockIndex)
            return false;
        if (!TryGetConstPush(in top, constants, out UInt256 a) || !TryGetConstPush(in under, constants, out UInt256 b))
            return false;

        int advance = under.Advance + top.Advance + size;
        if (advance > byte.MaxValue)
            return false;

        if (!TryApplyConstOperation(instruction, in a, in b, out UInt256 result))
            return false;

        constants.Add(result);
        pcToEntry[top.Pc] = InvalidEntry;
        pcToEntry[pc] = InvalidEntry;
        ops.RemoveAt(ops.Count - 1);
        ops[^1] = new StreamOp((byte)Instruction.PUSH32, under.Kind, under.Pc, under.BlockIndex, (byte)advance, (ulong)(constants.Count - 1));
        return true;
    }

    /// <summary>
    /// Computes a binary operation over two analysis-time constants, where <paramref name="a"/> is
    /// the operand the executor would consume first. ADD through SGT run the executor's own
    /// implementations so those cannot disagree on wrapping or division by zero; the bitwise ops
    /// and shifts are hand-mirrored from the executor's cores - the shift guard replicates
    /// ShiftCore's saturation test - so a change to either side must be made to both.
    /// </summary>
    private static bool TryApplyConstOperation(Instruction instruction, in UInt256 a, in UInt256 b, out UInt256 result)
    {
        switch (instruction)
        {
            case Instruction.ADD: EvmInstructions.OpAdd.Operation(in a, in b, out result); return true;
            case Instruction.SUB: EvmInstructions.OpSub.Operation(in a, in b, out result); return true;
            case Instruction.MUL: EvmInstructions.OpMul.Operation(in a, in b, out result); return true;
            case Instruction.DIV: EvmInstructions.OpDiv.Operation(in a, in b, out result); return true;
            case Instruction.SDIV: EvmInstructions.OpSDiv.Operation(in a, in b, out result); return true;
            case Instruction.MOD: EvmInstructions.OpMod.Operation(in a, in b, out result); return true;
            case Instruction.SMOD: EvmInstructions.OpSMod.Operation(in a, in b, out result); return true;
            case Instruction.LT: EvmInstructions.OpLt.Operation(in a, in b, out result); return true;
            case Instruction.GT: EvmInstructions.OpGt.Operation(in a, in b, out result); return true;
            case Instruction.SLT: EvmInstructions.OpSLt.Operation(in a, in b, out result); return true;
            case Instruction.SGT: EvmInstructions.OpSGt.Operation(in a, in b, out result); return true;
            case Instruction.AND: result = a & b; return true;
            case Instruction.OR: result = a | b; return true;
            case Instruction.XOR: result = a ^ b; return true;
            case Instruction.EQ: result = a == b ? UInt256.One : default; return true;
            case Instruction.SHL: result = !a.IsUint64 || a.u0 >= 256 ? default : b << (int)a.u0; return true;
            case Instruction.SHR: result = !a.IsUint64 || a.u0 >= 256 ? default : b >> (int)a.u0; return true;
            default: result = default; return false;
        }
    }

    /// <summary>A plain, unfused in-block push whose value analysis knows exactly.</summary>
    private static bool TryGetConstPush(in StreamOp entry, List<UInt256> constants, out UInt256 value)
    {
        value = default;
        if (entry.Kind is not (StreamOpKind.InBlock or StreamOpKind.BlockFirst))
            return false;

        Instruction opcode = (Instruction)entry.Opcode;
        if (opcode == Instruction.PUSH1 || (opcode >= Instruction.PUSH3 && opcode <= Instruction.PUSH8))
        {
            value = entry.Operand;
            return true;
        }

        if (opcode >= Instruction.PUSH9 && opcode <= Instruction.PUSH32)
        {
            value = constants[(int)entry.Operand];
            return true;
        }

        return false;
    }



    /// <summary>
    /// Fuses two adjacent glue ops into one entry when the previously emitted entry is the plain,
    /// unfused first half. Gas is unchanged (each half's static cost is already summed into the
    /// block) and the failure type matches per-op interpretation: the pair's single bounds check
    /// rejects exactly the stack depths at which one of the two halves would have failed.
    /// </summary>
    private static bool TryFuseGluePair(ReadOnlySpan<byte> code, List<StreamOp> ops, Instruction second, int pc, out StreamOp glued)
    {
        glued = default;
        if (ops.Count == 0) return false;

        StreamOp first = ops[^1];
        if (first.Kind is not (StreamOpKind.InBlock or StreamOpKind.BlockFirst)) return false;

        Instruction firstOp = (Instruction)first.Opcode;
        byte fused;
        ulong operand;
        if (firstOp == Instruction.POP && second == Instruction.POP)
        {
            fused = FusedOpcode.PopPop;
            operand = 0;
        }
        else if (firstOp == Instruction.PUSH1 && second == Instruction.PUSH1)
        {
            // Folding a constant into the following arithmetic op saves a dispatch AND a stack
            // round trip, so it outranks pairing the two pushes: leave the second PUSH1 alone when
            // the op after it would consume it.
            if (pc + 2 < code.Length && FusedOpcode.TryMap((Instruction)code[pc + 2], out _))
            {
                return false;
            }

            // Both immediates travel in the operand: the first PUSH's value in the low byte, this
            // one's in the next, and the executor pushes low then high in that order.
            fused = FusedOpcode.Push1Push1;
            operand = first.Operand | ((ulong)code[pc + 1] << 8);
        }
        else if (firstOp == Instruction.PUSH1 && second is >= Instruction.DUP1 and <= Instruction.DUP8)
        {
            // The immediate travels in the low byte and the dup depth in the next; the executor
            // pushes then duplicates, so depth one duplicates the value just pushed.
            fused = FusedOpcode.Push1Dup;
            operand = first.Operand | ((ulong)(second - Instruction.DUP1 + 1) << 8);
        }
        else if (second == Instruction.POP && firstOp is >= Instruction.SWAP1 and <= Instruction.SWAP8)
        {
            // Swap depth as the executor takes it: SWAP1 exchanges the top with the slot two down.
            fused = FusedOpcode.SwapPop;
            operand = (ulong)(firstOp - Instruction.SWAP1 + 2);
        }
        else if (first.Opcode == FusedOpcode.Shl && second == Instruction.SUB)
        {
            // The already-fused shift keeps its pooled constant and absorbs the subtraction, so the
            // shifted value never reaches the stack. Top pair on the wave-two histogram after the
            // glued pushes.
            fused = FusedOpcode.ShlSub;
            operand = first.Operand;
        }
        else if (firstOp == Instruction.SUB && second == Instruction.AND)
        {
            // Masking a difference is the second most frequent adjacent pair the first fusion wave
            // left behind on the measured workload, and both halves are plain in-block binary ops.
            fused = FusedOpcode.SubAnd;
            operand = 0;
        }
        else if (firstOp == Instruction.AND && second == Instruction.ISZERO)
        {
            fused = FusedOpcode.AndIsZero;
            operand = 0;
        }
        else
        {
            return false;
        }

        int size = second == Instruction.PUSH1 ? 2 : 1;
        glued = new StreamOp(fused, first.Kind, first.Pc, first.BlockIndex, (byte)(first.Advance + size), operand);
        return true;
    }

    private static bool CanCarryJumpDestGas(ReadOnlySpan<byte> code, int pc)
    {
        if ((uint)pc >= (uint)code.Length) return false;
        Instruction next = (Instruction)code[pc];
        return GetInBlockCost(next) != NotInBlock && pc + GetImmediateByteCount(next) < code.Length;
    }

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
