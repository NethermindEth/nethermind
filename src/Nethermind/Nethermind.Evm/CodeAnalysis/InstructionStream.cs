// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core;

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
    /// <summary>Any other op: standard table handler with the full per-op epilogue.</summary>
    Boundary = 4,
}

/// <summary>
/// One pre-decoded instruction (or fused PUSH+op pair) of an <see cref="InstructionStream"/>.
/// </summary>
public readonly struct StreamOp(byte opcode, StreamOpKind kind, ushort pc, ushort blockIndex, byte advance, ulong operand)
{
    /// <summary>Pre-decoded immediate of an in-block PUSH1..PUSH8 (big-endian value); for a
    /// fused pair this is the pushed constant the op consumes.</summary>
    public readonly ulong Operand = operand;
    /// <summary>For block-charging kinds: index into <see cref="InstructionStream.BlockGas"/>.</summary>
    public readonly ushort BlockIndex = blockIndex;
    public readonly ushort Pc = pc;
    /// <summary>For a fused pair: the second op's opcode (the push survives as <see cref="Operand"/>).</summary>
    public readonly byte Opcode = opcode;
    /// <summary>Total code bytes this entry covers (opcode + immediates; both for a fused pair).</summary>
    public readonly byte Advance = advance;
    public readonly StreamOpKind Kind = kind;
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
    /// <summary>Entry index for every entry-start pc; <see cref="InvalidEntry"/> for immediate
    /// bytes and fused-pair interiors; index one past the last op at pc == code length.</summary>
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
                if (openBlock >= 0 && IsConstFusable(instruction) && TryTakePrecedingPush(ops, out StreamOp push))
                {
                    // The pair becomes one entry: the push survives as the operand, the pc map
                    // forgets this op's own start (nothing can land inside a pair).
                    blockGas[openBlock] += cost;
                    pcToEntry[pc] = InvalidEntry;
                    StreamOpKind fusedKind = push.Kind == StreamOpKind.BlockFirst
                        ? StreamOpKind.FusedBlockFirst
                        : StreamOpKind.FusedInBlock;
                    ops[^1] = new StreamOp((byte)instruction, fusedKind, push.Pc, push.BlockIndex, (byte)(push.Advance + size), push.Operand);
                }
                else
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
                    ops.Add(new StreamOp((byte)instruction, kind, (ushort)pc, (ushort)openBlock, (byte)size, operand));
                }
            }
            else
            {
                // Includes JUMP/JUMPI/PUSH2 (the table keeps the fused PUSH2+JUMP handler) and
                // a trailing PUSH whose immediates are truncated by the end of code.
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

    /// <summary>
    /// Binary ops whose first operand is the stack top — a preceding in-block PUSH constant
    /// folds into them as a fused pair. Must match the executor's fused switch exactly.
    /// </summary>
    public static bool IsConstFusable(Instruction instruction)
        => instruction is Instruction.ADD or Instruction.SUB or Instruction.MUL or Instruction.DIV
            or Instruction.SDIV or Instruction.MOD or Instruction.SMOD
            or Instruction.LT or Instruction.GT or Instruction.SLT or Instruction.SGT
            or Instruction.EQ or Instruction.AND or Instruction.OR or Instruction.XOR
            or Instruction.SHL or Instruction.SHR;

    private static bool TryTakePrecedingPush(List<StreamOp> ops, out StreamOp push)
    {
        push = default;
        if (ops.Count == 0)
            return false;

        StreamOp last = ops[^1];
        if (last.Kind is not (StreamOpKind.BlockFirst or StreamOpKind.InBlock))
            return false;
        if ((Instruction)last.Opcode is not (Instruction.PUSH1 or >= Instruction.PUSH3 and <= Instruction.PUSH8))
            return false;

        push = last;
        return true;
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
