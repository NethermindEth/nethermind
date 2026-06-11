// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Evm.CodeAnalysis;

/// <summary>
/// How the stream executor dispatches a <see cref="StreamOp"/>. Order matters: the executor
/// tests "is this a precharged op" as <c>Kind &lt;= InBlock</c>.
/// </summary>
public enum StreamOpKind : byte
{
    /// <summary>First op of a basic block: charges the block's summed static gas, then runs
    /// its gas-free core.</summary>
    BlockFirst,
    /// <summary>Static-cost op inside a precharged block; runs the gas-free core.</summary>
    InBlock,
    /// <summary>Any other op: standard table handler with the full per-op epilogue.</summary>
    Boundary,
}

/// <summary>
/// One pre-decoded instruction of an <see cref="InstructionStream"/>.
/// </summary>
public readonly struct StreamOp(byte opcode, StreamOpKind kind, ushort pc, int blockIndex, ulong operand)
{
    /// <summary>Pre-decoded immediate for in-block PUSH1..PUSH8 (big-endian value).</summary>
    public readonly ulong Operand = operand;
    /// <summary>For <see cref="StreamOpKind.BlockFirst"/>: index into <see cref="InstructionStream.BlockGas"/>.</summary>
    public readonly int BlockIndex = blockIndex;
    public readonly ushort Pc = pc;
    public readonly byte Opcode = opcode;
    public readonly StreamOpKind Kind = kind;
}

/// <summary>
/// Bytecode preprocessed into a flat instruction stream with per-basic-block static gas sums,
/// built once per <see cref="CodeInfo"/> and shared by every execution of that code.
/// </summary>
/// <remarks>
/// Consensus invariants the analyzer maintains:
/// <list type="bullet">
/// <item>Only ops whose gas is a spec-independent constant at Shanghai+ are precharged
/// (<see cref="StreamOpKind.BlockFirst"/>/<see cref="StreamOpKind.InBlock"/>); callers must
/// gate stream execution on a tip-fork dispatch fingerprint.</item>
/// <item>A JUMPDEST is always a solo block, so a fused PUSH2+JUMP landing one past it never
/// skips an uncharged block prefix.</item>
/// <item>PUSH immediates are pre-decoded only when fully present in code; a truncated
/// trailing PUSH stays a boundary op so the table handler keeps its exact padding semantics.</item>
/// <item>The executor recomputes the entry index from the landing pc after every table call
/// (fused handlers consume multiple instructions) and re-meters any block entered past its
/// <see cref="StreamOpKind.BlockFirst"/>.</item>
/// </list>
/// </remarks>
public sealed class InstructionStream
{
    /// <summary>Entry-index sentinel for program counters that are not an instruction start.</summary>
    public const ushort InvalidEntry = ushort.MaxValue;

    public readonly StreamOp[] Ops;
    public readonly long[] BlockGas;
    /// <summary>Entry index for every instruction-start pc; <see cref="InvalidEntry"/> for
    /// immediate bytes; index one past the last op at pc == code length.</summary>
    public readonly ushort[] PcToEntry;

    private InstructionStream(StreamOp[] ops, long[] blockGas, ushort[] pcToEntry)
    {
        Ops = ops;
        BlockGas = blockGas;
        PcToEntry = pcToEntry;
    }

    public static InstructionStream? TryBuild(ReadOnlySpan<byte> code)
    {
        if (code.Length == 0 || code.Length >= ushort.MaxValue)
            return null;

        List<StreamOp> ops = new(code.Length / 2);
        List<long> blockGas = new(code.Length / 16);
        ushort[] pcToEntry = new ushort[code.Length + 1];
        pcToEntry.AsSpan().Fill(InvalidEntry);

        int openBlock = -1;
        int pc = 0;
        while (pc < code.Length)
        {
            Instruction instruction = (Instruction)code[pc];
            int immediates = GetImmediateByteCount(instruction);
            pcToEntry[pc] = (ushort)ops.Count;

            if (instruction == Instruction.JUMPDEST)
            {
                // Solo block: a fused PUSH2+JUMP lands one past the JUMPDEST having charged it
                // itself, so the ops that follow must sit in their own, separately charged block.
                blockGas.Add(GasCostOf.JumpDest);
                ops.Add(new StreamOp((byte)instruction, StreamOpKind.BlockFirst, (ushort)pc, blockGas.Count - 1, 0));
                openBlock = -1;
            }
            else if (TryGetInBlockCost(instruction, out long cost) && pc + immediates < code.Length)
            {
                ulong operand = instruction is >= Instruction.PUSH1 and <= Instruction.PUSH8
                    ? ReadImmediate(code.Slice(pc + 1, immediates))
                    : 0;

                StreamOpKind kind = StreamOpKind.InBlock;
                if (openBlock < 0)
                {
                    blockGas.Add(0);
                    openBlock = blockGas.Count - 1;
                    kind = StreamOpKind.BlockFirst;
                }

                blockGas[openBlock] += cost;
                ops.Add(new StreamOp((byte)instruction, kind, (ushort)pc, openBlock, operand));
            }
            else
            {
                // Includes JUMP/JUMPI/PUSH2 (the table keeps the fused PUSH2+JUMP handler) and
                // a trailing PUSH whose immediates are truncated by the end of code.
                openBlock = -1;
                ops.Add(new StreamOp((byte)instruction, StreamOpKind.Boundary, (ushort)pc, 0, 0));
            }

            pc += 1 + immediates;
        }

        // Entry indexes live in the ushort pc map; oversized streams fall back to the
        // bytecode loop.
        if (ops.Count >= InvalidEntry)
            return null;

        pcToEntry[code.Length] = (ushort)ops.Count;
        return new InstructionStream(ops.ToArray(), blockGas.ToArray(), pcToEntry);
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
            case >= Instruction.PUSH3 and <= Instruction.PUSH8:
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

    private static ulong ReadImmediate(ReadOnlySpan<byte> immediates)
    {
        Span<byte> padded = stackalloc byte[sizeof(ulong)];
        immediates.CopyTo(padded.Slice(sizeof(ulong) - immediates.Length));
        return BinaryPrimitives.ReadUInt64BigEndian(padded);
    }

    private static int GetImmediateByteCount(Instruction instruction)
        => instruction is >= Instruction.PUSH1 and <= Instruction.PUSH32
            ? instruction - Instruction.PUSH1 + 1
            : 0;
}
